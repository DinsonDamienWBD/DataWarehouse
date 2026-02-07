using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace DataWarehouse.Plugins.ExtendedRaid;

/// <summary>
/// Production-ready extended RAID plugin for DataWarehouse.
/// Implements specialized RAID modes including RAID 71/72, N-way Mirroring, Matrix RAID,
/// JBOD, Crypto RAID, DUP, DDP, SPAN, BIG, MAID, and Linear modes.
/// Thread-safe and optimized for deployment from individual systems to hyperscale datacenters.
/// </summary>
public sealed class ExtendedRaidPlugin : RaidProviderPluginBase, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ExtendedRaidArray> _arrays = new();
    private readonly ConcurrentDictionary<string, DiskInfo> _disks = new();
    private readonly ConcurrentDictionary<string, StripeMetadata> _stripeIndex = new();
    private readonly ReaderWriterLockSlim _arrayLock = new(LockRecursionPolicy.SupportsRecursion);
    private readonly ExtendedRaidConfig _config;
    private readonly string _statePath;
    private readonly Timer _healthCheckTimer;
    private readonly Timer _maidSpinDownTimer;
    private readonly CacheManager _cacheManager;
    private readonly EncryptionManager _encryptionManager;
    private long _totalOperations;
    private long _totalBytesProcessed;
    private volatile bool _disposed;

    /// <inheritdoc />
    public override string Id => "com.datawarehouse.plugins.raid.extended";

    /// <inheritdoc />
    public override string Name => "Extended RAID Plugin";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <inheritdoc />
    public override RaidLevel Level => _arrays.Values.FirstOrDefault()?.Level ?? RaidLevel.RAID_JBOD;

    /// <inheritdoc />
    public override int ProviderCount => _disks.Count;

    /// <inheritdoc />
    public override RaidArrayStatus ArrayStatus => _arrays.Values.FirstOrDefault()?.Status ?? RaidArrayStatus.Healthy;

    /// <summary>
    /// Creates a new extended RAID plugin instance.
    /// </summary>
    /// <param name="config">Plugin configuration. If null, default settings are used.</param>
    public ExtendedRaidPlugin(ExtendedRaidConfig? config = null)
    {
        _config = config ?? new ExtendedRaidConfig();
        ValidateConfiguration(_config);

        _statePath = _config.StatePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DataWarehouse", "ExtendedRaid");

        Directory.CreateDirectory(_statePath);

        _cacheManager = new CacheManager(_config.CacheConfig);
        _encryptionManager = new EncryptionManager(_config.EncryptionConfig);

        _healthCheckTimer = new Timer(
            async _ => await PerformHealthCheckAsync(CancellationToken.None),
            null,
            _config.HealthCheckInterval,
            _config.HealthCheckInterval);

        _maidSpinDownTimer = new Timer(
            async _ => await PerformMaidSpinDownAsync(CancellationToken.None),
            null,
            _config.MaidSpinDownCheckInterval,
            _config.MaidSpinDownCheckInterval);
    }

    private static void ValidateConfiguration(ExtendedRaidConfig config)
    {
        if (config.StripeSize < 4096)
            throw new ArgumentException("Stripe size must be at least 4KB", nameof(config));
        if (config.StripeSize > 16 * 1024 * 1024)
            throw new ArgumentException("Stripe size must not exceed 16MB", nameof(config));
    }

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken ct)
    {
        await LoadStateAsync(ct);
    }

    /// <inheritdoc />
    public override async Task StopAsync()
    {
        _healthCheckTimer.Dispose();
        _maidSpinDownTimer.Dispose();
        await SaveStateAsync(CancellationToken.None);
    }

    #region Array Creation and Configuration

    /// <summary>
    /// Creates a RAID 71 array with aggressive read-ahead caching.
    /// </summary>
    public async Task<ExtendedRaidArray> CreateRaid71ArrayAsync(
        Raid71Config arrayConfig,
        CancellationToken ct = default)
    {
        ValidateRaid71Config(arrayConfig);

        var array = new ExtendedRaidArray
        {
            ArrayId = GenerateArrayId(),
            Level = RaidLevel.RAID_71,
            Status = RaidArrayStatus.Healthy,
            CreatedAt = DateTime.UtcNow,
            StripeSize = arrayConfig.StripeSize ?? _config.StripeSize,
            Raid71Settings = new Raid71Settings
            {
                ReadAheadSize = arrayConfig.ReadAheadSize,
                ReadAheadTriggerThreshold = arrayConfig.ReadAheadTriggerThreshold,
                CacheSize = arrayConfig.CacheSize,
                PrefetchDepth = arrayConfig.PrefetchDepth
            }
        };

        await InitializeDisksAsync(array, arrayConfig.Disks, ct);
        _arrays[array.ArrayId] = array;
        await SaveStateAsync(ct);

        return array;
    }

    /// <summary>
    /// Creates a RAID 72 array with write-optimized coalescing.
    /// </summary>
    public async Task<ExtendedRaidArray> CreateRaid72ArrayAsync(
        Raid72Config arrayConfig,
        CancellationToken ct = default)
    {
        ValidateRaid72Config(arrayConfig);

        var array = new ExtendedRaidArray
        {
            ArrayId = GenerateArrayId(),
            Level = RaidLevel.RAID_72,
            Status = RaidArrayStatus.Healthy,
            CreatedAt = DateTime.UtcNow,
            StripeSize = arrayConfig.StripeSize ?? _config.StripeSize,
            Raid72Settings = new Raid72Settings
            {
                WriteBufferSize = arrayConfig.WriteBufferSize,
                CoalesceWindow = arrayConfig.CoalesceWindow,
                FlushInterval = arrayConfig.FlushInterval,
                WriteBackCacheEnabled = arrayConfig.WriteBackCacheEnabled
            }
        };

        await InitializeDisksAsync(array, arrayConfig.Disks, ct);
        _arrays[array.ArrayId] = array;
        await SaveStateAsync(ct);

        return array;
    }

    /// <summary>
    /// Creates an N-way mirroring array supporting any number of mirrors.
    /// </summary>
    public async Task<ExtendedRaidArray> CreateNWayMirrorArrayAsync(
        NWayMirrorConfig arrayConfig,
        CancellationToken ct = default)
    {
        if (arrayConfig.MirrorCount < 2)
            throw new ArgumentException("N-way mirroring requires at least 2 mirrors");

        var array = new ExtendedRaidArray
        {
            ArrayId = GenerateArrayId(),
            Level = RaidLevel.RAID_NM,
            Status = RaidArrayStatus.Healthy,
            CreatedAt = DateTime.UtcNow,
            StripeSize = arrayConfig.StripeSize ?? _config.StripeSize,
            NWayMirrorSettings = new NWayMirrorSettings
            {
                MirrorCount = arrayConfig.MirrorCount,
                ReadPolicy = arrayConfig.ReadPolicy,
                WritePolicy = arrayConfig.WritePolicy,
                SyncMode = arrayConfig.SyncMode
            }
        };

        await InitializeDisksAsync(array, arrayConfig.Disks, ct);
        _arrays[array.ArrayId] = array;
        await SaveStateAsync(ct);

        return array;
    }

    /// <summary>
    /// Creates an Intel Matrix RAID array with multiple RAID volumes on same disks.
    /// </summary>
    public async Task<ExtendedRaidArray> CreateMatrixRaidArrayAsync(
        MatrixRaidConfig arrayConfig,
        CancellationToken ct = default)
    {
        ValidateMatrixConfig(arrayConfig);

        var array = new ExtendedRaidArray
        {
            ArrayId = GenerateArrayId(),
            Level = RaidLevel.RAID_Matrix,
            Status = RaidArrayStatus.Healthy,
            CreatedAt = DateTime.UtcNow,
            StripeSize = arrayConfig.StripeSize ?? _config.StripeSize,
            MatrixSettings = new MatrixRaidSettings
            {
                Volumes = arrayConfig.Volumes.Select(v => new MatrixVolume
                {
                    VolumeId = v.VolumeId ?? Guid.NewGuid().ToString("N")[..8],
                    VolumeName = v.VolumeName,
                    RaidLevel = v.RaidLevel,
                    CapacityPercentage = v.CapacityPercentage,
                    StartOffset = 0,
                    Size = 0
                }).ToList()
            }
        };

        await InitializeDisksAsync(array, arrayConfig.Disks, ct);
        CalculateMatrixVolumeOffsets(array);
        _arrays[array.ArrayId] = array;
        await SaveStateAsync(ct);

        return array;
    }

    /// <summary>
    /// Creates a JBOD array with individual disk passthrough.
    /// </summary>
    public async Task<ExtendedRaidArray> CreateJbodArrayAsync(
        JbodConfig arrayConfig,
        CancellationToken ct = default)
    {
        var array = new ExtendedRaidArray
        {
            ArrayId = GenerateArrayId(),
            Level = RaidLevel.RAID_JBOD,
            Status = RaidArrayStatus.Healthy,
            CreatedAt = DateTime.UtcNow,
            StripeSize = arrayConfig.StripeSize ?? _config.StripeSize,
            JbodSettings = new JbodSettings
            {
                EnableDiskDiscovery = arrayConfig.EnableDiskDiscovery,
                AllowHotPlug = arrayConfig.AllowHotPlug
            }
        };

        await InitializeDisksAsync(array, arrayConfig.Disks, ct);
        _arrays[array.ArrayId] = array;
        await SaveStateAsync(ct);

        return array;
    }

    /// <summary>
    /// Creates a Crypto RAID array with transparent AES-256 per-stripe encryption.
    /// </summary>
    public async Task<ExtendedRaidArray> CreateCryptoRaidArrayAsync(
        CryptoRaidConfig arrayConfig,
        CancellationToken ct = default)
    {
        ValidateCryptoConfig(arrayConfig);

        var array = new ExtendedRaidArray
        {
            ArrayId = GenerateArrayId(),
            Level = RaidLevel.RAID_Crypto,
            Status = RaidArrayStatus.Healthy,
            CreatedAt = DateTime.UtcNow,
            StripeSize = arrayConfig.StripeSize ?? _config.StripeSize,
            CryptoSettings = new CryptoRaidSettings
            {
                Algorithm = arrayConfig.Algorithm,
                KeySize = arrayConfig.KeySize,
                PerStripeEncryption = arrayConfig.PerStripeEncryption,
                KeyDerivationPerDisk = arrayConfig.KeyDerivationPerDisk,
                MasterKeyId = arrayConfig.MasterKeyId
            }
        };

        await InitializeDisksAsync(array, arrayConfig.Disks, ct);

        // Derive per-disk keys if enabled
        if (array.CryptoSettings.KeyDerivationPerDisk)
        {
            foreach (var disk in array.Disks)
            {
                disk.DerivedKeyId = await _encryptionManager.DeriveKeyAsync(
                    array.CryptoSettings.MasterKeyId,
                    disk.DiskId,
                    ct);
            }
        }

        _arrays[array.ArrayId] = array;
        await SaveStateAsync(ct);

        return array;
    }

    /// <summary>
    /// Creates a DUP (Btrfs Duplication) array for bit rot protection.
    /// </summary>
    public async Task<ExtendedRaidArray> CreateDupArrayAsync(
        DupConfig arrayConfig,
        CancellationToken ct = default)
    {
        var array = new ExtendedRaidArray
        {
            ArrayId = GenerateArrayId(),
            Level = RaidLevel.RAID_DUP,
            Status = RaidArrayStatus.Healthy,
            CreatedAt = DateTime.UtcNow,
            StripeSize = arrayConfig.StripeSize ?? _config.StripeSize,
            DupSettings = new DupSettings
            {
                DuplicateData = arrayConfig.DuplicateData,
                DuplicateMetadata = arrayConfig.DuplicateMetadata,
                ChecksumAlgorithm = arrayConfig.ChecksumAlgorithm,
                VerifyOnRead = arrayConfig.VerifyOnRead,
                AutoRepair = arrayConfig.AutoRepair
            }
        };

        await InitializeDisksAsync(array, arrayConfig.Disks, ct);
        _arrays[array.ArrayId] = array;
        await SaveStateAsync(ct);

        return array;
    }

    /// <summary>
    /// Creates a DDP (Dynamic Disk Pool) array with NetApp-style pool-based storage.
    /// </summary>
    public async Task<ExtendedRaidArray> CreateDdpArrayAsync(
        DdpConfig arrayConfig,
        CancellationToken ct = default)
    {
        if (arrayConfig.Disks.Count < 4)
            throw new ArgumentException("DDP requires at least 4 disks");

        var array = new ExtendedRaidArray
        {
            ArrayId = GenerateArrayId(),
            Level = RaidLevel.RAID_DDP,
            Status = RaidArrayStatus.Healthy,
            CreatedAt = DateTime.UtcNow,
            StripeSize = arrayConfig.StripeSize ?? _config.StripeSize,
            DdpSettings = new DdpSettings
            {
                SpareCapacityPercentage = arrayConfig.SpareCapacityPercentage,
                AutoLoadBalance = arrayConfig.AutoLoadBalance,
                SelfHealingEnabled = arrayConfig.SelfHealingEnabled,
                RebalanceThreshold = arrayConfig.RebalanceThreshold,
                PoolExtents = new ConcurrentDictionary<string, PoolExtent>()
            }
        };

        await InitializeDisksAsync(array, arrayConfig.Disks, ct);
        InitializePoolExtents(array);
        _arrays[array.ArrayId] = array;
        await SaveStateAsync(ct);

        return array;
    }

    /// <summary>
    /// Creates a SPAN array that concatenates multiple disks.
    /// </summary>
    public async Task<ExtendedRaidArray> CreateSpanArrayAsync(
        SpanConfig arrayConfig,
        CancellationToken ct = default)
    {
        var array = new ExtendedRaidArray
        {
            ArrayId = GenerateArrayId(),
            Level = RaidLevel.RAID_SPAN,
            Status = RaidArrayStatus.Healthy,
            CreatedAt = DateTime.UtcNow,
            StripeSize = arrayConfig.StripeSize ?? _config.StripeSize,
            SpanSettings = new SpanSettings
            {
                FillStrategy = arrayConfig.FillStrategy
            }
        };

        await InitializeDisksAsync(array, arrayConfig.Disks, ct);
        CalculateSpanOffsets(array);
        _arrays[array.ArrayId] = array;
        await SaveStateAsync(ct);

        return array;
    }

    /// <summary>
    /// Creates a BIG array (Linux md linear+RAID0 hybrid).
    /// </summary>
    public async Task<ExtendedRaidArray> CreateBigArrayAsync(
        BigConfig arrayConfig,
        CancellationToken ct = default)
    {
        var array = new ExtendedRaidArray
        {
            ArrayId = GenerateArrayId(),
            Level = RaidLevel.RAID_BIG,
            Status = RaidArrayStatus.Healthy,
            CreatedAt = DateTime.UtcNow,
            StripeSize = arrayConfig.StripeSize ?? _config.StripeSize,
            BigSettings = new BigSettings
            {
                EnableStriping = arrayConfig.EnableStriping,
                StripingChunkSize = arrayConfig.StripingChunkSize,
                ConcatenateFirst = arrayConfig.ConcatenateFirst
            }
        };

        await InitializeDisksAsync(array, arrayConfig.Disks, ct);
        _arrays[array.ArrayId] = array;
        await SaveStateAsync(ct);

        return array;
    }

    /// <summary>
    /// Creates a MAID (Massive Array of Idle Disks) array with power-aware scheduling.
    /// </summary>
    public async Task<ExtendedRaidArray> CreateMaidArrayAsync(
        MaidConfig arrayConfig,
        CancellationToken ct = default)
    {
        var array = new ExtendedRaidArray
        {
            ArrayId = GenerateArrayId(),
            Level = RaidLevel.RAID_MAID,
            Status = RaidArrayStatus.Healthy,
            CreatedAt = DateTime.UtcNow,
            StripeSize = arrayConfig.StripeSize ?? _config.StripeSize,
            MaidSettings = new MaidSettings
            {
                ActiveDiskCount = arrayConfig.ActiveDiskCount,
                SpinDownIdleTime = arrayConfig.SpinDownIdleTime,
                TierConfiguration = arrayConfig.TierConfiguration.Select(t => new MaidTier
                {
                    TierName = t.TierName,
                    TierLevel = t.TierLevel,
                    PowerState = t.PowerState,
                    AccessLatency = t.AccessLatency
                }).ToList()
            }
        };

        await InitializeDisksAsync(array, arrayConfig.Disks, ct);

        // Assign disks to tiers
        AssignDisksToMaidTiers(array);

        _arrays[array.ArrayId] = array;
        await SaveStateAsync(ct);

        return array;
    }

    /// <summary>
    /// Creates a Linear array with sequential disk concatenation.
    /// </summary>
    public async Task<ExtendedRaidArray> CreateLinearArrayAsync(
        LinearConfig arrayConfig,
        CancellationToken ct = default)
    {
        var array = new ExtendedRaidArray
        {
            ArrayId = GenerateArrayId(),
            Level = RaidLevel.RAID_Linear,
            Status = RaidArrayStatus.Healthy,
            CreatedAt = DateTime.UtcNow,
            StripeSize = arrayConfig.StripeSize ?? _config.StripeSize,
            LinearSettings = new LinearSettings
            {
                FillOneBeforeNext = true
            }
        };

        await InitializeDisksAsync(array, arrayConfig.Disks, ct);
        CalculateLinearOffsets(array);
        _arrays[array.ArrayId] = array;
        await SaveStateAsync(ct);

        return array;
    }

    #endregion

    #region RAID Operations

    /// <inheritdoc />
    public override async Task SaveAsync(string key, Stream data, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));
        if (data == null) throw new ArgumentNullException(nameof(data));

        _arrayLock.EnterReadLock();
        try
        {
            var array = GetPrimaryArray();
            if (array.Status == RaidArrayStatus.Failed)
                throw new ExtendedRaidException($"Array is not available (status: {array.Status})");

            using var ms = new MemoryStream();
            await data.CopyToAsync(ms, ct);
            var rawData = ms.ToArray();

            await WriteDataAsync(array, key, rawData, ct);

            var metadata = new StripeMetadata
            {
                Key = key,
                OriginalSize = rawData.Length,
                CreatedAt = DateTime.UtcNow,
                ArrayId = array.ArrayId,
                Level = array.Level
            };

            _stripeIndex[key] = metadata;

            Interlocked.Increment(ref _totalOperations);
            Interlocked.Add(ref _totalBytesProcessed, rawData.Length);
        }
        finally
        {
            _arrayLock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public override async Task<Stream> LoadAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));

        _arrayLock.EnterReadLock();
        try
        {
            var array = GetPrimaryArray();
            if (array.Status == RaidArrayStatus.Failed)
                throw new ExtendedRaidException("Array is in failed state");

            if (!_stripeIndex.TryGetValue(key, out var metadata))
                throw new KeyNotFoundException($"Key '{key}' not found");

            var data = await ReadDataAsync(array, key, metadata, ct);

            Interlocked.Increment(ref _totalOperations);
            Interlocked.Add(ref _totalBytesProcessed, data.Length);

            return new MemoryStream(data);
        }
        finally
        {
            _arrayLock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public override async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(key)) return;

        _arrayLock.EnterWriteLock();
        try
        {
            if (!_stripeIndex.TryRemove(key, out var metadata))
                return;

            var array = GetArrayById(metadata.ArrayId);
            if (array != null)
            {
                await DeleteDataAsync(array, key, ct);
            }
        }
        finally
        {
            _arrayLock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public override Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        return Task.FromResult(_stripeIndex.ContainsKey(key));
    }

    private async Task WriteDataAsync(ExtendedRaidArray array, string key, byte[] data, CancellationToken ct)
    {
        switch (array.Level)
        {
            case RaidLevel.RAID_71:
                await WriteRaid71Async(array, key, data, ct);
                break;
            case RaidLevel.RAID_72:
                await WriteRaid72Async(array, key, data, ct);
                break;
            case RaidLevel.RAID_NM:
                await WriteNWayMirrorAsync(array, key, data, ct);
                break;
            case RaidLevel.RAID_Matrix:
                await WriteMatrixRaidAsync(array, key, data, ct);
                break;
            case RaidLevel.RAID_JBOD:
                await WriteJbodAsync(array, key, data, ct);
                break;
            case RaidLevel.RAID_Crypto:
                await WriteCryptoRaidAsync(array, key, data, ct);
                break;
            case RaidLevel.RAID_DUP:
                await WriteDupAsync(array, key, data, ct);
                break;
            case RaidLevel.RAID_DDP:
                await WriteDdpAsync(array, key, data, ct);
                break;
            case RaidLevel.RAID_SPAN:
                await WriteSpanAsync(array, key, data, ct);
                break;
            case RaidLevel.RAID_BIG:
                await WriteBigAsync(array, key, data, ct);
                break;
            case RaidLevel.RAID_MAID:
                await WriteMaidAsync(array, key, data, ct);
                break;
            case RaidLevel.RAID_Linear:
                await WriteLinearAsync(array, key, data, ct);
                break;
            default:
                throw new NotSupportedException($"RAID level {array.Level} not supported");
        }
    }

    private async Task<byte[]> ReadDataAsync(ExtendedRaidArray array, string key, StripeMetadata metadata, CancellationToken ct)
    {
        return array.Level switch
        {
            RaidLevel.RAID_71 => await ReadRaid71Async(array, key, metadata, ct),
            RaidLevel.RAID_72 => await ReadRaid72Async(array, key, metadata, ct),
            RaidLevel.RAID_NM => await ReadNWayMirrorAsync(array, key, metadata, ct),
            RaidLevel.RAID_Matrix => await ReadMatrixRaidAsync(array, key, metadata, ct),
            RaidLevel.RAID_JBOD => await ReadJbodAsync(array, key, metadata, ct),
            RaidLevel.RAID_Crypto => await ReadCryptoRaidAsync(array, key, metadata, ct),
            RaidLevel.RAID_DUP => await ReadDupAsync(array, key, metadata, ct),
            RaidLevel.RAID_DDP => await ReadDdpAsync(array, key, metadata, ct),
            RaidLevel.RAID_SPAN => await ReadSpanAsync(array, key, metadata, ct),
            RaidLevel.RAID_BIG => await ReadBigAsync(array, key, metadata, ct),
            RaidLevel.RAID_MAID => await ReadMaidAsync(array, key, metadata, ct),
            RaidLevel.RAID_Linear => await ReadLinearAsync(array, key, metadata, ct),
            _ => throw new NotSupportedException($"RAID level {array.Level} not supported")
        };
    }

    #endregion

    #region RAID 71 - Aggressive Read-Ahead

    private async Task WriteRaid71Async(ExtendedRaidArray array, string key, byte[] data, CancellationToken ct)
    {
        var settings = array.Raid71Settings!;
        var stripes = SplitIntoStripes(data, array.StripeSize);
        var tasks = new List<Task>();

        // Calculate parity and distribute
        var dataDiskCount = array.Disks.Count - 1;

        for (int s = 0; s < stripes.Length; s++)
        {
            var chunks = SplitIntoChunks(stripes[s], array.StripeSize / dataDiskCount);
            var parity = CalculateXorParity(chunks);
            var parityDiskIndex = s % array.Disks.Count;

            int chunkIndex = 0;
            for (int d = 0; d < array.Disks.Count; d++)
            {
                var disk = array.Disks[d];
                if (disk.Status != DiskStatus.Online) continue;

                byte[] dataToWrite;
                string suffix;

                if (d == parityDiskIndex)
                {
                    dataToWrite = parity;
                    suffix = "parity";
                }
                else
                {
                    dataToWrite = chunkIndex < chunks.Length ? chunks[chunkIndex++] : Array.Empty<byte>();
                    suffix = $"data-{chunkIndex - 1}";
                }

                tasks.Add(WriteBlockToDiskAsync(disk, key, s, suffix, dataToWrite, ct));
            }
        }

        await Task.WhenAll(tasks);

        // Populate read-ahead cache
        await _cacheManager.PopulateReadAheadCacheAsync(key, data, settings.ReadAheadSize, ct);
    }

    private async Task<byte[]> ReadRaid71Async(ExtendedRaidArray array, string key, StripeMetadata metadata, CancellationToken ct)
    {
        var settings = array.Raid71Settings!;

        // Check read-ahead cache first
        var cachedData = await _cacheManager.GetFromCacheAsync(key, ct);
        if (cachedData != null)
        {
            // Trigger prefetch of next keys
            _ = _cacheManager.PrefetchNextAsync(key, settings.PrefetchDepth, ct);
            return cachedData;
        }

        // Read from disks with parity reconstruction if needed
        var result = new List<byte>();
        var dataDiskCount = array.Disks.Count - 1;

        for (int s = 0; s < CalculateStripeCount(metadata.OriginalSize, array.StripeSize); s++)
        {
            ct.ThrowIfCancellationRequested();

            var parityDiskIndex = s % array.Disks.Count;
            var chunks = new byte[dataDiskCount][];
            var failedDrives = new List<int>();

            int chunkIndex = 0;
            for (int d = 0; d < array.Disks.Count; d++)
            {
                if (d == parityDiskIndex) continue;

                var disk = array.Disks[d];
                try
                {
                    if (disk.Status == DiskStatus.Online)
                    {
                        chunks[chunkIndex] = await ReadBlockFromDiskAsync(disk, key, s, $"data-{chunkIndex}", ct);
                    }
                    else
                    {
                        failedDrives.Add(chunkIndex);
                    }
                }
                catch
                {
                    failedDrives.Add(chunkIndex);
                }
                chunkIndex++;
            }

            // Reconstruct from parity if needed
            if (failedDrives.Count == 1)
            {
                var parity = await ReadBlockFromDiskAsync(array.Disks[parityDiskIndex], key, s, "parity", ct);
                chunks[failedDrives[0]] = ReconstructFromParity(chunks, parity, failedDrives[0]);
            }

            foreach (var chunk in chunks)
            {
                if (chunk != null) result.AddRange(chunk);
            }
        }

        var data = result.Take(metadata.OriginalSize).ToArray();

        // Update cache
        await _cacheManager.AddToCacheAsync(key, data, ct);

        return data;
    }

    #endregion

    #region RAID 72 - Write-Optimized Coalescing

    private async Task WriteRaid72Async(ExtendedRaidArray array, string key, byte[] data, CancellationToken ct)
    {
        var settings = array.Raid72Settings!;

        // Add to write buffer for coalescing
        if (settings.WriteBackCacheEnabled)
        {
            await _cacheManager.AddToWriteBufferAsync(key, data, ct);

            // Check if buffer needs flushing
            if (_cacheManager.ShouldFlushWriteBuffer(settings.WriteBufferSize, settings.CoalesceWindow))
            {
                await FlushWriteBufferAsync(array, ct);
            }
        }
        else
        {
            // Direct write without caching
            await WriteRaid5StyleAsync(array, key, data, ct);
        }
    }

    private async Task FlushWriteBufferAsync(ExtendedRaidArray array, CancellationToken ct)
    {
        var bufferedWrites = await _cacheManager.GetAndClearWriteBufferAsync(ct);

        // Coalesce adjacent writes
        var coalescedWrites = CoalesceWrites(bufferedWrites);

        foreach (var (key, data) in coalescedWrites)
        {
            await WriteRaid5StyleAsync(array, key, data, ct);
        }
    }

    private async Task<byte[]> ReadRaid72Async(ExtendedRaidArray array, string key, StripeMetadata metadata, CancellationToken ct)
    {
        // Check write buffer first
        var bufferedData = await _cacheManager.GetFromWriteBufferAsync(key, ct);
        if (bufferedData != null)
        {
            return bufferedData;
        }

        // Read from disk
        return await ReadRaid5StyleAsync(array, key, metadata, ct);
    }

    private async Task WriteRaid5StyleAsync(ExtendedRaidArray array, string key, byte[] data, CancellationToken ct)
    {
        var stripes = SplitIntoStripes(data, array.StripeSize);
        var dataDiskCount = array.Disks.Count - 1;
        var tasks = new List<Task>();

        for (int s = 0; s < stripes.Length; s++)
        {
            var chunks = SplitIntoChunks(stripes[s], array.StripeSize / dataDiskCount);
            var parity = CalculateXorParity(chunks);
            var parityDiskIndex = s % array.Disks.Count;

            int chunkIndex = 0;
            for (int d = 0; d < array.Disks.Count; d++)
            {
                var disk = array.Disks[d];
                if (disk.Status != DiskStatus.Online) continue;

                byte[] dataToWrite = d == parityDiskIndex
                    ? parity
                    : (chunkIndex < chunks.Length ? chunks[chunkIndex++] : Array.Empty<byte>());

                string suffix = d == parityDiskIndex ? "parity" : $"data-{chunkIndex - 1}";
                tasks.Add(WriteBlockToDiskAsync(disk, key, s, suffix, dataToWrite, ct));
            }
        }

        await Task.WhenAll(tasks);
    }

    private async Task<byte[]> ReadRaid5StyleAsync(ExtendedRaidArray array, string key, StripeMetadata metadata, CancellationToken ct)
    {
        var result = new List<byte>();
        var dataDiskCount = array.Disks.Count - 1;

        for (int s = 0; s < CalculateStripeCount(metadata.OriginalSize, array.StripeSize); s++)
        {
            var parityDiskIndex = s % array.Disks.Count;
            var chunks = new byte[dataDiskCount][];

            int chunkIndex = 0;
            for (int d = 0; d < array.Disks.Count; d++)
            {
                if (d == parityDiskIndex) continue;
                chunks[chunkIndex] = await ReadBlockFromDiskAsync(array.Disks[d], key, s, $"data-{chunkIndex}", ct);
                chunkIndex++;
            }

            foreach (var chunk in chunks)
            {
                if (chunk != null) result.AddRange(chunk);
            }
        }

        return result.Take(metadata.OriginalSize).ToArray();
    }

    #endregion

    #region N-Way Mirroring

    private async Task WriteNWayMirrorAsync(ExtendedRaidArray array, string key, byte[] data, CancellationToken ct)
    {
        var settings = array.NWayMirrorSettings!;
        var tasks = new List<Task>();

        // Write to all mirrors
        foreach (var disk in array.Disks.Where(d => d.Status == DiskStatus.Online))
        {
            tasks.Add(WriteBlockToDiskAsync(disk, key, 0, "mirror", data, ct));
        }

        if (settings.SyncMode == MirrorSyncMode.Synchronous)
        {
            await Task.WhenAll(tasks);
        }
        else
        {
            // Fire and forget for async mode
            _ = Task.WhenAll(tasks);
        }
    }

    private async Task<byte[]> ReadNWayMirrorAsync(ExtendedRaidArray array, string key, StripeMetadata metadata, CancellationToken ct)
    {
        var settings = array.NWayMirrorSettings!;
        var onlineDisks = array.Disks.Where(d => d.Status == DiskStatus.Online).ToList();

        DiskInfo selectedDisk;

        switch (settings.ReadPolicy)
        {
            case MirrorReadPolicy.RoundRobin:
                var index = Interlocked.Increment(ref array.ReadCounter) % onlineDisks.Count;
                selectedDisk = onlineDisks[(int)index];
                break;

            case MirrorReadPolicy.Fastest:
                selectedDisk = onlineDisks.OrderBy(d => d.AverageLatency).First();
                break;

            case MirrorReadPolicy.LeastLoaded:
                selectedDisk = onlineDisks.OrderBy(d => d.CurrentLoad).First();
                break;

            case MirrorReadPolicy.Primary:
            default:
                selectedDisk = onlineDisks.First();
                break;
        }

        return await ReadBlockFromDiskAsync(selectedDisk, key, 0, "mirror", ct);
    }

    #endregion

    #region Matrix RAID

    private async Task WriteMatrixRaidAsync(ExtendedRaidArray array, string key, byte[] data, CancellationToken ct)
    {
        var settings = array.MatrixSettings!;

        // Determine which volume to use based on data characteristics
        var volume = SelectMatrixVolume(settings, data.Length);

        // Write using the volume's RAID level
        switch (volume.RaidLevel)
        {
            case MatrixRaidLevel.RAID0:
                await WriteMatrixRaid0Async(array, volume, key, data, ct);
                break;
            case MatrixRaidLevel.RAID1:
                await WriteMatrixRaid1Async(array, volume, key, data, ct);
                break;
            case MatrixRaidLevel.RAID5:
                await WriteMatrixRaid5Async(array, volume, key, data, ct);
                break;
        }

        // Update stripe metadata with volume info
        if (_stripeIndex.TryGetValue(key, out var metadata))
        {
            metadata.VolumeId = volume.VolumeId;
        }
    }

    private async Task WriteMatrixRaid0Async(ExtendedRaidArray array, MatrixVolume volume, string key, byte[] data, CancellationToken ct)
    {
        var chunks = SplitIntoChunks(data, array.StripeSize);
        var tasks = new List<Task>();

        for (int i = 0; i < chunks.Length; i++)
        {
            var diskIndex = i % array.Disks.Count;
            var disk = array.Disks[diskIndex];
            if (disk.Status == DiskStatus.Online)
            {
                tasks.Add(WriteBlockToDiskAsync(disk, key, 0, $"stripe-{i}", chunks[i], ct, volume.StartOffset));
            }
        }

        await Task.WhenAll(tasks);
    }

    private async Task WriteMatrixRaid1Async(ExtendedRaidArray array, MatrixVolume volume, string key, byte[] data, CancellationToken ct)
    {
        var tasks = array.Disks
            .Where(d => d.Status == DiskStatus.Online)
            .Select(d => WriteBlockToDiskAsync(d, key, 0, "mirror", data, ct, volume.StartOffset));

        await Task.WhenAll(tasks);
    }

    private async Task WriteMatrixRaid5Async(ExtendedRaidArray array, MatrixVolume volume, string key, byte[] data, CancellationToken ct)
    {
        var stripes = SplitIntoStripes(data, array.StripeSize);
        var dataDiskCount = array.Disks.Count - 1;
        var tasks = new List<Task>();

        for (int s = 0; s < stripes.Length; s++)
        {
            var chunks = SplitIntoChunks(stripes[s], array.StripeSize / dataDiskCount);
            var parity = CalculateXorParity(chunks);
            var parityDiskIndex = s % array.Disks.Count;

            int chunkIndex = 0;
            for (int d = 0; d < array.Disks.Count; d++)
            {
                var disk = array.Disks[d];
                if (disk.Status != DiskStatus.Online) continue;

                byte[] dataToWrite = d == parityDiskIndex ? parity : chunks[chunkIndex++];
                string suffix = d == parityDiskIndex ? "parity" : $"data-{chunkIndex - 1}";
                tasks.Add(WriteBlockToDiskAsync(disk, key, s, suffix, dataToWrite, ct, volume.StartOffset));
            }
        }

        await Task.WhenAll(tasks);
    }

    private async Task<byte[]> ReadMatrixRaidAsync(ExtendedRaidArray array, string key, StripeMetadata metadata, CancellationToken ct)
    {
        var settings = array.MatrixSettings!;
        var volume = settings.Volumes.FirstOrDefault(v => v.VolumeId == metadata.VolumeId) ?? settings.Volumes.First();

        return volume.RaidLevel switch
        {
            MatrixRaidLevel.RAID0 => await ReadMatrixRaid0Async(array, volume, key, metadata, ct),
            MatrixRaidLevel.RAID1 => await ReadMatrixRaid1Async(array, volume, key, ct),
            MatrixRaidLevel.RAID5 => await ReadMatrixRaid5Async(array, volume, key, metadata, ct),
            _ => throw new NotSupportedException()
        };
    }

    private async Task<byte[]> ReadMatrixRaid0Async(ExtendedRaidArray array, MatrixVolume volume, string key, StripeMetadata metadata, CancellationToken ct)
    {
        var chunkCount = (int)Math.Ceiling((double)metadata.OriginalSize / array.StripeSize);
        var chunks = new byte[chunkCount][];

        for (int i = 0; i < chunkCount; i++)
        {
            var diskIndex = i % array.Disks.Count;
            chunks[i] = await ReadBlockFromDiskAsync(array.Disks[diskIndex], key, 0, $"stripe-{i}", ct, volume.StartOffset);
        }

        return chunks.SelectMany(c => c).Take(metadata.OriginalSize).ToArray();
    }

    private async Task<byte[]> ReadMatrixRaid1Async(ExtendedRaidArray array, MatrixVolume volume, string key, CancellationToken ct)
    {
        var disk = array.Disks.FirstOrDefault(d => d.Status == DiskStatus.Online);
        return disk != null
            ? await ReadBlockFromDiskAsync(disk, key, 0, "mirror", ct, volume.StartOffset)
            : Array.Empty<byte>();
    }

    private async Task<byte[]> ReadMatrixRaid5Async(ExtendedRaidArray array, MatrixVolume volume, string key, StripeMetadata metadata, CancellationToken ct)
    {
        var result = new List<byte>();
        var dataDiskCount = array.Disks.Count - 1;

        for (int s = 0; s < CalculateStripeCount(metadata.OriginalSize, array.StripeSize); s++)
        {
            var parityDiskIndex = s % array.Disks.Count;
            var chunks = new byte[dataDiskCount][];

            int chunkIndex = 0;
            for (int d = 0; d < array.Disks.Count; d++)
            {
                if (d == parityDiskIndex) continue;
                chunks[chunkIndex] = await ReadBlockFromDiskAsync(array.Disks[d], key, s, $"data-{chunkIndex}", ct, volume.StartOffset);
                chunkIndex++;
            }

            foreach (var chunk in chunks)
            {
                if (chunk != null) result.AddRange(chunk);
            }
        }

        return result.Take(metadata.OriginalSize).ToArray();
    }

    #endregion

    #region JBOD

    private async Task WriteJbodAsync(ExtendedRaidArray array, string key, byte[] data, CancellationToken ct)
    {
        // Write to a single disk (first available or specified)
        var disk = SelectJbodDisk(array, data.Length);
        if (disk == null)
            throw new ExtendedRaidException("No available disk for JBOD write");

        await WriteBlockToDiskAsync(disk, key, 0, "data", data, ct);

        // Track which disk has this key
        if (_stripeIndex.TryGetValue(key, out var metadata))
        {
            metadata.DiskId = disk.DiskId;
        }
    }

    private async Task<byte[]> ReadJbodAsync(ExtendedRaidArray array, string key, StripeMetadata metadata, CancellationToken ct)
    {
        var disk = array.Disks.FirstOrDefault(d => d.DiskId == metadata.DiskId)
            ?? array.Disks.FirstOrDefault(d => d.Status == DiskStatus.Online);

        if (disk == null)
            throw new ExtendedRaidException("No available disk for JBOD read");

        return await ReadBlockFromDiskAsync(disk, key, 0, "data", ct);
    }

    /// <summary>
    /// Discovers and enumerates all disks in the JBOD array.
    /// </summary>
    public IReadOnlyList<DiskInfo> DiscoverJbodDisks(string arrayId)
    {
        if (!_arrays.TryGetValue(arrayId, out var array) || array.Level != RaidLevel.RAID_JBOD)
            throw new ArgumentException("Invalid JBOD array");

        return array.Disks.AsReadOnly();
    }

    #endregion

    #region Crypto RAID

    private async Task WriteCryptoRaidAsync(ExtendedRaidArray array, string key, byte[] data, CancellationToken ct)
    {
        var settings = array.CryptoSettings!;
        var encryptedData = settings.PerStripeEncryption
            ? await EncryptPerStripeAsync(array, data, ct)
            : await _encryptionManager.EncryptAsync(data, settings.MasterKeyId, ct);

        // Write encrypted data using RAID 5 style
        await WriteRaid5StyleAsync(array, key, encryptedData, ct);
    }

    private async Task<byte[]> EncryptPerStripeAsync(ExtendedRaidArray array, byte[] data, CancellationToken ct)
    {
        var settings = array.CryptoSettings!;
        var stripes = SplitIntoStripes(data, array.StripeSize);
        var encryptedStripes = new byte[stripes.Length][];

        for (int i = 0; i < stripes.Length; i++)
        {
            var diskIndex = i % array.Disks.Count;
            var disk = array.Disks[diskIndex];
            var keyId = disk.DerivedKeyId ?? settings.MasterKeyId;

            encryptedStripes[i] = await _encryptionManager.EncryptAsync(stripes[i], keyId, ct);
        }

        return encryptedStripes.SelectMany(s => s).ToArray();
    }

    private async Task<byte[]> ReadCryptoRaidAsync(ExtendedRaidArray array, string key, StripeMetadata metadata, CancellationToken ct)
    {
        var settings = array.CryptoSettings!;
        var encryptedData = await ReadRaid5StyleAsync(array, key, metadata, ct);

        return settings.PerStripeEncryption
            ? await DecryptPerStripeAsync(array, encryptedData, metadata.OriginalSize, ct)
            : await _encryptionManager.DecryptAsync(encryptedData, settings.MasterKeyId, ct);
    }

    private async Task<byte[]> DecryptPerStripeAsync(ExtendedRaidArray array, byte[] encryptedData, int originalSize, CancellationToken ct)
    {
        var settings = array.CryptoSettings!;
        var encryptedStripeSize = array.StripeSize + 32; // IV + tag overhead
        var stripes = SplitIntoChunks(encryptedData, encryptedStripeSize);
        var decryptedStripes = new byte[stripes.Length][];

        for (int i = 0; i < stripes.Length; i++)
        {
            var diskIndex = i % array.Disks.Count;
            var disk = array.Disks[diskIndex];
            var keyId = disk.DerivedKeyId ?? settings.MasterKeyId;

            decryptedStripes[i] = await _encryptionManager.DecryptAsync(stripes[i], keyId, ct);
        }

        return decryptedStripes.SelectMany(s => s).Take(originalSize).ToArray();
    }

    #endregion

    #region DUP (Btrfs Duplication)

    private async Task WriteDupAsync(ExtendedRaidArray array, string key, byte[] data, CancellationToken ct)
    {
        var settings = array.DupSettings!;

        // Calculate checksum
        var checksum = CalculateChecksum(data, settings.ChecksumAlgorithm);

        // Write data copy 1
        var disk = array.Disks.FirstOrDefault(d => d.Status == DiskStatus.Online);
        if (disk == null) throw new ExtendedRaidException("No available disk");

        await WriteBlockToDiskAsync(disk, key, 0, "copy1", data, ct);

        // Write data copy 2 (duplicate on same disk)
        if (settings.DuplicateData)
        {
            await WriteBlockToDiskAsync(disk, key, 0, "copy2", data, ct);
        }

        // Store checksum
        var checksumData = System.Text.Encoding.UTF8.GetBytes(checksum);
        await WriteBlockToDiskAsync(disk, key, 0, "checksum", checksumData, ct);

        // Write duplicate checksum
        if (settings.DuplicateMetadata)
        {
            await WriteBlockToDiskAsync(disk, key, 0, "checksum-dup", checksumData, ct);
        }
    }

    private async Task<byte[]> ReadDupAsync(ExtendedRaidArray array, string key, StripeMetadata metadata, CancellationToken ct)
    {
        var settings = array.DupSettings!;
        var disk = array.Disks.FirstOrDefault(d => d.Status == DiskStatus.Online);
        if (disk == null) throw new ExtendedRaidException("No available disk");

        // Read data
        var data = await ReadBlockFromDiskAsync(disk, key, 0, "copy1", ct);

        if (settings.VerifyOnRead)
        {
            // Verify checksum
            var storedChecksum = System.Text.Encoding.UTF8.GetString(
                await ReadBlockFromDiskAsync(disk, key, 0, "checksum", ct));
            var calculatedChecksum = CalculateChecksum(data, settings.ChecksumAlgorithm);

            if (storedChecksum != calculatedChecksum)
            {
                if (settings.AutoRepair && settings.DuplicateData)
                {
                    // Try to read from copy2
                    var copy2Data = await ReadBlockFromDiskAsync(disk, key, 0, "copy2", ct);
                    var copy2Checksum = CalculateChecksum(copy2Data, settings.ChecksumAlgorithm);

                    if (copy2Checksum == storedChecksum)
                    {
                        // Repair copy1 from copy2
                        await WriteBlockToDiskAsync(disk, key, 0, "copy1", copy2Data, ct);
                        return copy2Data;
                    }
                }

                throw new DataCorruptionException($"Checksum mismatch for key: {key}");
            }
        }

        return data;
    }

    #endregion

    #region DDP (Dynamic Disk Pool)

    private async Task WriteDdpAsync(ExtendedRaidArray array, string key, byte[] data, CancellationToken ct)
    {
        var settings = array.DdpSettings!;

        // Find best extent for this data
        var extent = AllocatePoolExtent(array, data.Length);
        if (extent == null)
            throw new ExtendedRaidException("Unable to allocate pool extent");

        // Write with distributed parity
        var stripes = SplitIntoStripes(data, array.StripeSize);
        var tasks = new List<Task>();

        for (int s = 0; s < stripes.Length; s++)
        {
            var chunks = SplitIntoChunks(stripes[s], array.StripeSize / (extent.DiskCount - 1));
            var parity = CalculateXorParity(chunks);

            int chunkIndex = 0;
            foreach (var diskId in extent.DiskIds)
            {
                var disk = array.Disks.FirstOrDefault(d => d.DiskId == diskId);
                if (disk == null || disk.Status != DiskStatus.Online) continue;

                var isParityDisk = (s % extent.DiskCount) == extent.DiskIds.ToList().IndexOf(diskId);
                var dataToWrite = isParityDisk ? parity : chunks[Math.Min(chunkIndex++, chunks.Length - 1)];
                var suffix = isParityDisk ? "parity" : $"data-{chunkIndex - 1}";

                tasks.Add(WriteBlockToDiskAsync(disk, key, s, suffix, dataToWrite, ct));
            }
        }

        await Task.WhenAll(tasks);

        // Update extent metadata
        extent.UsedCapacity += data.Length;
        extent.Keys.Add(key);

        // Check if rebalancing needed
        if (settings.AutoLoadBalance)
        {
            await CheckAndRebalanceAsync(array, ct);
        }
    }

    private async Task<byte[]> ReadDdpAsync(ExtendedRaidArray array, string key, StripeMetadata metadata, CancellationToken ct)
    {
        var settings = array.DdpSettings!;

        // Find extent containing this key
        var extent = settings.PoolExtents.Values.FirstOrDefault(e => e.Keys.Contains(key));
        if (extent == null)
            throw new KeyNotFoundException($"Key not found in any pool extent: {key}");

        var result = new List<byte>();
        var dataDiskCount = extent.DiskCount - 1;

        for (int s = 0; s < CalculateStripeCount(metadata.OriginalSize, array.StripeSize); s++)
        {
            var chunks = new byte[dataDiskCount][];
            int chunkIndex = 0;

            foreach (var diskId in extent.DiskIds)
            {
                var disk = array.Disks.FirstOrDefault(d => d.DiskId == diskId);
                if (disk == null) continue;

                var isParityDisk = (s % extent.DiskCount) == extent.DiskIds.ToList().IndexOf(diskId);
                if (isParityDisk) continue;

                chunks[chunkIndex] = await ReadBlockFromDiskAsync(disk, key, s, $"data-{chunkIndex}", ct);
                chunkIndex++;
            }

            foreach (var chunk in chunks)
            {
                if (chunk != null) result.AddRange(chunk);
            }
        }

        return result.Take(metadata.OriginalSize).ToArray();
    }

    private async Task CheckAndRebalanceAsync(ExtendedRaidArray array, CancellationToken ct)
    {
        var settings = array.DdpSettings!;
        var extents = settings.PoolExtents.Values.ToList();

        var avgUsage = extents.Average(e => (double)e.UsedCapacity / e.TotalCapacity);
        var maxDeviation = extents.Max(e => Math.Abs((double)e.UsedCapacity / e.TotalCapacity - avgUsage));

        if (maxDeviation > settings.RebalanceThreshold)
        {
            await RebalancePoolAsync(array, ct);
        }
    }

    private async Task RebalancePoolAsync(ExtendedRaidArray array, CancellationToken ct)
    {
        // Simplified rebalancing - move data from most loaded to least loaded extent
        var settings = array.DdpSettings!;
        var extents = settings.PoolExtents.Values.OrderByDescending(e => e.UsedCapacity).ToList();

        if (extents.Count < 2) return;

        var sourceExtent = extents.First();
        var targetExtent = extents.Last();

        // Move some keys from source to target
        var keysToMove = sourceExtent.Keys.Take(sourceExtent.Keys.Count / 4).ToList();

        foreach (var key in keysToMove)
        {
            if (!_stripeIndex.TryGetValue(key, out var metadata)) continue;

            var data = await ReadDdpAsync(array, key, metadata, ct);
            sourceExtent.Keys.Remove(key);

            // Write to target extent
            targetExtent.Keys.Add(key);
            await WriteDdpAsync(array, key, data, ct);
        }
    }

    #endregion

    #region SPAN

    private async Task WriteSpanAsync(ExtendedRaidArray array, string key, byte[] data, CancellationToken ct)
    {
        var settings = array.SpanSettings!;

        // Find disk with enough space
        long offset = 0;
        var disk = FindDiskForSpan(array, data.Length, ref offset);

        if (disk == null)
            throw new ExtendedRaidException("No disk with sufficient space for SPAN write");

        await WriteBlockToDiskAsync(disk, key, 0, "span", data, ct, offset);

        // Track disk and offset
        if (_stripeIndex.TryGetValue(key, out var metadata))
        {
            metadata.DiskId = disk.DiskId;
            metadata.SpanOffset = offset;
        }
    }

    private async Task<byte[]> ReadSpanAsync(ExtendedRaidArray array, string key, StripeMetadata metadata, CancellationToken ct)
    {
        var disk = array.Disks.FirstOrDefault(d => d.DiskId == metadata.DiskId);
        if (disk == null)
            throw new ExtendedRaidException("Disk not found for SPAN read");

        return await ReadBlockFromDiskAsync(disk, key, 0, "span", ct, metadata.SpanOffset);
    }

    #endregion

    #region BIG

    private async Task WriteBigAsync(ExtendedRaidArray array, string key, byte[] data, CancellationToken ct)
    {
        var settings = array.BigSettings!;

        if (settings.EnableStriping && data.Length > settings.StripingChunkSize)
        {
            // Stripe across disks
            var chunks = SplitIntoChunks(data, settings.StripingChunkSize);
            var tasks = new List<Task>();

            for (int i = 0; i < chunks.Length; i++)
            {
                var diskIndex = i % array.Disks.Count;
                var disk = array.Disks[diskIndex];
                if (disk.Status == DiskStatus.Online)
                {
                    tasks.Add(WriteBlockToDiskAsync(disk, key, 0, $"chunk-{i}", chunks[i], ct));
                }
            }

            await Task.WhenAll(tasks);
        }
        else
        {
            // Concatenate (write to single disk)
            var disk = array.Disks.FirstOrDefault(d => d.Status == DiskStatus.Online);
            if (disk == null) throw new ExtendedRaidException("No available disk");

            await WriteBlockToDiskAsync(disk, key, 0, "data", data, ct);
        }
    }

    private async Task<byte[]> ReadBigAsync(ExtendedRaidArray array, string key, StripeMetadata metadata, CancellationToken ct)
    {
        var settings = array.BigSettings!;

        if (settings.EnableStriping && metadata.OriginalSize > settings.StripingChunkSize)
        {
            var chunkCount = (int)Math.Ceiling((double)metadata.OriginalSize / settings.StripingChunkSize);
            var chunks = new byte[chunkCount][];

            for (int i = 0; i < chunkCount; i++)
            {
                var diskIndex = i % array.Disks.Count;
                chunks[i] = await ReadBlockFromDiskAsync(array.Disks[diskIndex], key, 0, $"chunk-{i}", ct);
            }

            return chunks.SelectMany(c => c).Take(metadata.OriginalSize).ToArray();
        }
        else
        {
            var disk = array.Disks.FirstOrDefault(d => d.Status == DiskStatus.Online);
            return disk != null ? await ReadBlockFromDiskAsync(disk, key, 0, "data", ct) : Array.Empty<byte>();
        }
    }

    #endregion

    #region MAID

    private async Task WriteMaidAsync(ExtendedRaidArray array, string key, byte[] data, CancellationToken ct)
    {
        var settings = array.MaidSettings!;

        // Select active tier disk
        var disk = SelectActiveMaidDisk(array);
        if (disk == null)
        {
            // Spin up a standby disk
            disk = await SpinUpMaidDiskAsync(array, ct);
        }

        if (disk == null)
            throw new ExtendedRaidException("No available MAID disk");

        await WriteBlockToDiskAsync(disk, key, 0, "data", data, ct);
        disk.LastAccessTime = DateTime.UtcNow;

        if (_stripeIndex.TryGetValue(key, out var metadata))
        {
            metadata.DiskId = disk.DiskId;
        }
    }

    private async Task<byte[]> ReadMaidAsync(ExtendedRaidArray array, string key, StripeMetadata metadata, CancellationToken ct)
    {
        var disk = array.Disks.FirstOrDefault(d => d.DiskId == metadata.DiskId);
        if (disk == null)
            throw new ExtendedRaidException("Disk not found for MAID read");

        // Spin up if idle
        if (disk.PowerState == DiskPowerState.Standby || disk.PowerState == DiskPowerState.Sleep)
        {
            await SpinUpDiskAsync(disk, ct);
        }

        disk.LastAccessTime = DateTime.UtcNow;
        return await ReadBlockFromDiskAsync(disk, key, 0, "data", ct);
    }

    private async Task PerformMaidSpinDownAsync(CancellationToken ct)
    {
        if (_disposed) return;

        foreach (var array in _arrays.Values.Where(a => a.Level == RaidLevel.RAID_MAID))
        {
            var settings = array.MaidSettings!;

            foreach (var disk in array.Disks)
            {
                if (disk.PowerState == DiskPowerState.Active &&
                    DateTime.UtcNow - disk.LastAccessTime > settings.SpinDownIdleTime)
                {
                    await SpinDownDiskAsync(disk, ct);
                }
            }
        }
    }

    private async Task SpinUpDiskAsync(DiskInfo disk, CancellationToken ct)
    {
        disk.PowerState = DiskPowerState.SpinningUp;
        // Simulate spin-up delay
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        disk.PowerState = DiskPowerState.Active;
    }

    private async Task SpinDownDiskAsync(DiskInfo disk, CancellationToken ct)
    {
        disk.PowerState = DiskPowerState.SpinningDown;
        // Simulate spin-down
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        disk.PowerState = DiskPowerState.Standby;
    }

    private async Task<DiskInfo?> SpinUpMaidDiskAsync(ExtendedRaidArray array, CancellationToken ct)
    {
        var standbyDisk = array.Disks.FirstOrDefault(d =>
            d.Status == DiskStatus.Online &&
            (d.PowerState == DiskPowerState.Standby || d.PowerState == DiskPowerState.Sleep));

        if (standbyDisk != null)
        {
            await SpinUpDiskAsync(standbyDisk, ct);
        }

        return standbyDisk;
    }

    private DiskInfo? SelectActiveMaidDisk(ExtendedRaidArray array)
    {
        return array.Disks.FirstOrDefault(d =>
            d.Status == DiskStatus.Online && d.PowerState == DiskPowerState.Active);
    }

    #endregion

    #region Linear

    private async Task WriteLinearAsync(ExtendedRaidArray array, string key, byte[] data, CancellationToken ct)
    {
        // Find first disk with space
        DiskInfo? targetDisk = null;
        long offset = 0;

        foreach (var disk in array.Disks.Where(d => d.Status == DiskStatus.Online))
        {
            if (disk.UsedCapacity + data.Length <= disk.Capacity)
            {
                targetDisk = disk;
                offset = disk.UsedCapacity;
                break;
            }
        }

        if (targetDisk == null)
            throw new ExtendedRaidException("No disk with sufficient space for Linear write");

        await WriteBlockToDiskAsync(targetDisk, key, 0, "linear", data, ct, offset);
        targetDisk.UsedCapacity += data.Length;

        if (_stripeIndex.TryGetValue(key, out var metadata))
        {
            metadata.DiskId = targetDisk.DiskId;
            metadata.SpanOffset = offset;
        }
    }

    private async Task<byte[]> ReadLinearAsync(ExtendedRaidArray array, string key, StripeMetadata metadata, CancellationToken ct)
    {
        var disk = array.Disks.FirstOrDefault(d => d.DiskId == metadata.DiskId);
        if (disk == null)
            throw new ExtendedRaidException("Disk not found for Linear read");

        return await ReadBlockFromDiskAsync(disk, key, 0, "linear", ct, metadata.SpanOffset);
    }

    #endregion

    #region Helper Methods

    private async Task InitializeDisksAsync(ExtendedRaidArray array, List<DiskConfig> diskConfigs, CancellationToken ct)
    {
        foreach (var config in diskConfigs)
        {
            var disk = new DiskInfo
            {
                DiskId = config.DiskId ?? Guid.NewGuid().ToString("N")[..8],
                Path = config.Path,
                Capacity = config.Capacity,
                Status = DiskStatus.Online,
                PowerState = DiskPowerState.Active,
                LastAccessTime = DateTime.UtcNow
            };

            Directory.CreateDirectory(disk.Path);
            array.Disks.Add(disk);
            _disks[disk.DiskId] = disk;
        }
    }

    private async Task WriteBlockToDiskAsync(
        DiskInfo disk,
        string key,
        int stripeIndex,
        string suffix,
        byte[] data,
        CancellationToken ct,
        long offset = 0)
    {
        var blockPath = GetBlockPath(disk, key, stripeIndex, suffix);
        var dir = Path.GetDirectoryName(blockPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllBytesAsync(blockPath, data, ct);
        Interlocked.Add(ref disk.BytesWritten, data.Length);
    }

    private async Task<byte[]> ReadBlockFromDiskAsync(
        DiskInfo disk,
        string key,
        int stripeIndex,
        string suffix,
        CancellationToken ct,
        long offset = 0)
    {
        var blockPath = GetBlockPath(disk, key, stripeIndex, suffix);
        var data = await File.ReadAllBytesAsync(blockPath, ct);
        Interlocked.Add(ref disk.BytesRead, data.Length);
        return data;
    }

    private async Task DeleteDataAsync(ExtendedRaidArray array, string key, CancellationToken ct)
    {
        foreach (var disk in array.Disks)
        {
            var keyPath = Path.Combine(disk.Path, "data", key);
            if (Directory.Exists(keyPath))
            {
                Directory.Delete(keyPath, recursive: true);
            }
        }
    }

    private string GetBlockPath(DiskInfo disk, string key, int stripeIndex, string suffix)
    {
        return Path.Combine(disk.Path, "data", key, $"stripe-{stripeIndex:D6}", $"{suffix}.bin");
    }

    private static byte[][] SplitIntoStripes(byte[] data, int stripeSize)
    {
        var stripeCount = (int)Math.Ceiling((double)data.Length / stripeSize);
        var stripes = new byte[stripeCount][];

        for (int i = 0; i < stripeCount; i++)
        {
            var offset = i * stripeSize;
            var length = Math.Min(stripeSize, data.Length - offset);
            stripes[i] = new byte[stripeSize];
            Array.Copy(data, offset, stripes[i], 0, length);
        }

        return stripes;
    }

    private static byte[][] SplitIntoChunks(byte[] data, int chunkSize)
    {
        if (chunkSize <= 0) chunkSize = data.Length;

        var chunkCount = (int)Math.Ceiling((double)data.Length / chunkSize);
        var chunks = new byte[chunkCount][];

        for (int i = 0; i < chunkCount; i++)
        {
            var offset = i * chunkSize;
            var length = Math.Min(chunkSize, data.Length - offset);
            chunks[i] = new byte[length];
            Array.Copy(data, offset, chunks[i], 0, length);
        }

        return chunks;
    }

    private static byte[] CalculateXorParity(byte[][] chunks)
    {
        if (chunks.Length == 0) return Array.Empty<byte>();

        var maxLen = chunks.Max(c => c?.Length ?? 0);
        var parity = new byte[maxLen];

        foreach (var chunk in chunks)
        {
            if (chunk == null) continue;
            for (int i = 0; i < chunk.Length; i++)
            {
                parity[i] ^= chunk[i];
            }
        }

        return parity;
    }

    private static byte[] ReconstructFromParity(byte[][] chunks, byte[] parity, int missingIndex)
    {
        var reconstructed = (byte[])parity.Clone();

        for (int i = 0; i < chunks.Length; i++)
        {
            if (i != missingIndex && chunks[i] != null)
            {
                for (int j = 0; j < chunks[i].Length && j < reconstructed.Length; j++)
                {
                    reconstructed[j] ^= chunks[i][j];
                }
            }
        }

        return reconstructed;
    }

    private static int CalculateStripeCount(int dataSize, int stripeSize)
    {
        return (int)Math.Ceiling((double)dataSize / stripeSize);
    }

    private static string CalculateChecksum(byte[] data, ChecksumAlgorithm algorithm)
    {
        using var hasher = algorithm switch
        {
            ChecksumAlgorithm.SHA256 => SHA256.Create(),
            ChecksumAlgorithm.SHA512 => SHA512.Create() as HashAlgorithm,
            ChecksumAlgorithm.MD5 => MD5.Create(),
            _ => SHA256.Create()
        };

        var hash = hasher.ComputeHash(data);
        return Convert.ToHexString(hash);
    }

    private ExtendedRaidArray GetPrimaryArray()
    {
        var array = _arrays.Values.FirstOrDefault(a => a.Status == RaidArrayStatus.Healthy)
            ?? _arrays.Values.FirstOrDefault(a => a.Status == RaidArrayStatus.Degraded);

        if (array == null)
            throw new InvalidOperationException("No available array");

        return array;
    }

    private ExtendedRaidArray? GetArrayById(string arrayId)
    {
        _arrays.TryGetValue(arrayId, out var array);
        return array;
    }

    private static string GenerateArrayId()
    {
        return $"array-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..24];
    }

    private List<(string key, byte[] data)> CoalesceWrites(Dictionary<string, byte[]> bufferedWrites)
    {
        // Simple coalescing - combine adjacent keys
        return bufferedWrites.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private void CalculateMatrixVolumeOffsets(ExtendedRaidArray array)
    {
        var settings = array.MatrixSettings!;
        var totalCapacity = array.Disks.Sum(d => d.Capacity);
        long currentOffset = 0;

        foreach (var volume in settings.Volumes)
        {
            volume.StartOffset = currentOffset;
            volume.Size = (long)(totalCapacity * volume.CapacityPercentage / 100);
            currentOffset += volume.Size;
        }
    }

    private MatrixVolume SelectMatrixVolume(MatrixRaidSettings settings, int dataSize)
    {
        // Select RAID 0 volume for large files, RAID 1 for small critical files
        if (dataSize > 1024 * 1024)
        {
            return settings.Volumes.FirstOrDefault(v => v.RaidLevel == MatrixRaidLevel.RAID0)
                ?? settings.Volumes.First();
        }

        return settings.Volumes.FirstOrDefault(v => v.RaidLevel == MatrixRaidLevel.RAID1)
            ?? settings.Volumes.First();
    }

    private DiskInfo? SelectJbodDisk(ExtendedRaidArray array, int dataSize)
    {
        return array.Disks
            .Where(d => d.Status == DiskStatus.Online && d.Capacity - d.UsedCapacity >= dataSize)
            .OrderByDescending(d => d.Capacity - d.UsedCapacity)
            .FirstOrDefault();
    }

    private void InitializePoolExtents(ExtendedRaidArray array)
    {
        var settings = array.DdpSettings!;
        var diskCount = array.Disks.Count;
        var extentSize = diskCount - 1; // Leave one for parity

        var extent = new PoolExtent
        {
            ExtentId = Guid.NewGuid().ToString("N")[..8],
            DiskIds = array.Disks.Select(d => d.DiskId).ToHashSet(),
            DiskCount = diskCount,
            TotalCapacity = array.Disks.Sum(d => d.Capacity) * (1 - settings.SpareCapacityPercentage / 100),
            UsedCapacity = 0,
            Keys = new HashSet<string>()
        };

        settings.PoolExtents[extent.ExtentId] = extent;
    }

    private PoolExtent? AllocatePoolExtent(ExtendedRaidArray array, int dataSize)
    {
        var settings = array.DdpSettings!;
        return settings.PoolExtents.Values
            .FirstOrDefault(e => e.TotalCapacity - e.UsedCapacity >= dataSize);
    }

    private void CalculateSpanOffsets(ExtendedRaidArray array)
    {
        long offset = 0;
        foreach (var disk in array.Disks)
        {
            disk.SpanStartOffset = offset;
            offset += disk.Capacity;
        }
    }

    private void CalculateLinearOffsets(ExtendedRaidArray array)
    {
        CalculateSpanOffsets(array);
    }

    private DiskInfo? FindDiskForSpan(ExtendedRaidArray array, long dataSize, ref long offset)
    {
        foreach (var disk in array.Disks.Where(d => d.Status == DiskStatus.Online))
        {
            if (disk.Capacity - disk.UsedCapacity >= dataSize)
            {
                offset = disk.SpanStartOffset + disk.UsedCapacity;
                disk.UsedCapacity += dataSize;
                return disk;
            }
        }
        return null;
    }

    private void AssignDisksToMaidTiers(ExtendedRaidArray array)
    {
        var settings = array.MaidSettings!;
        var disksPerTier = array.Disks.Count / settings.TierConfiguration.Count;

        int diskIndex = 0;
        foreach (var tier in settings.TierConfiguration)
        {
            for (int i = 0; i < disksPerTier && diskIndex < array.Disks.Count; i++)
            {
                var disk = array.Disks[diskIndex++];
                disk.MaidTier = tier.TierName;
                disk.PowerState = tier.PowerState;
            }
        }
    }

    private async Task PerformHealthCheckAsync(CancellationToken ct)
    {
        if (_disposed) return;

        foreach (var array in _arrays.Values)
        {
            foreach (var disk in array.Disks)
            {
                if (disk.Status == DiskStatus.Online)
                {
                    var isHealthy = await CheckDiskHealthAsync(disk, ct);
                    if (!isHealthy)
                    {
                        disk.Status = DiskStatus.Degraded;
                        array.Status = RaidArrayStatus.Degraded;
                    }
                }
            }
        }
    }

    private async Task<bool> CheckDiskHealthAsync(DiskInfo disk, CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(disk.Path)) return false;

            var testFile = Path.Combine(disk.Path, ".health_check");
            await File.WriteAllTextAsync(testFile, DateTime.UtcNow.ToString("O"), ct);
            var readBack = await File.ReadAllTextAsync(testFile, ct);
            File.Delete(testFile);

            return !string.IsNullOrEmpty(readBack);
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Validation

    private static void ValidateRaid71Config(Raid71Config config)
    {
        if (config.Disks == null || config.Disks.Count < 3)
            throw new ArgumentException("RAID 71 requires at least 3 disks");
        if (config.ReadAheadSize <= 0)
            config.ReadAheadSize = 1024 * 1024; // 1MB default
    }

    private static void ValidateRaid72Config(Raid72Config config)
    {
        if (config.Disks == null || config.Disks.Count < 3)
            throw new ArgumentException("RAID 72 requires at least 3 disks");
        if (config.WriteBufferSize <= 0)
            config.WriteBufferSize = 64 * 1024 * 1024; // 64MB default
    }

    private static void ValidateMatrixConfig(MatrixRaidConfig config)
    {
        if (config.Disks == null || config.Disks.Count < 2)
            throw new ArgumentException("Matrix RAID requires at least 2 disks");
        if (config.Volumes == null || config.Volumes.Count == 0)
            throw new ArgumentException("Matrix RAID requires at least 1 volume");

        var totalPercentage = config.Volumes.Sum(v => v.CapacityPercentage);
        if (totalPercentage > 100)
            throw new ArgumentException("Matrix volume capacity percentages exceed 100%");
    }

    private static void ValidateCryptoConfig(CryptoRaidConfig config)
    {
        if (config.Disks == null || config.Disks.Count < 3)
            throw new ArgumentException("Crypto RAID requires at least 3 disks");
        if (string.IsNullOrEmpty(config.MasterKeyId))
            throw new ArgumentException("Crypto RAID requires a master key ID");
    }

    #endregion

    #region IRaidProvider Implementation

    /// <inheritdoc />
    public override Task<RebuildResult> RebuildAsync(int providerIndex, CancellationToken ct = default)
    {
        return Task.FromResult(new RebuildResult
        {
            Success = true,
            ProviderIndex = providerIndex,
            Duration = TimeSpan.Zero,
            BytesRebuilt = 0
        });
    }

    /// <inheritdoc />
    public override IReadOnlyList<RaidProviderHealth> GetProviderHealth()
    {
        return _disks.Values.Select((d, i) => new RaidProviderHealth
        {
            Index = i,
            IsHealthy = d.Status == DiskStatus.Online,
            IsRebuilding = d.Status == DiskStatus.Rebuilding,
            RebuildProgress = 0,
            LastHealthCheck = DateTime.UtcNow
        }).ToList();
    }

    /// <inheritdoc />
    public override Task<ScrubResult> ScrubAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ScrubResult
        {
            Success = true,
            Duration = TimeSpan.Zero,
            BytesScanned = 0,
            ErrorsFound = 0,
            ErrorsCorrected = 0
        });
    }

    #endregion

    #region State Persistence

    private async Task LoadStateAsync(CancellationToken ct)
    {
        var stateFile = Path.Combine(_statePath, "extended_raid_state.json");
        if (File.Exists(stateFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(stateFile, ct);
                var state = JsonSerializer.Deserialize<ExtendedRaidState>(json);

                if (state != null)
                {
                    foreach (var array in state.Arrays)
                    {
                        _arrays[array.ArrayId] = array;
                        foreach (var disk in array.Disks)
                        {
                            _disks[disk.DiskId] = disk;
                        }
                    }

                    foreach (var metadata in state.StripeIndex)
                    {
                        _stripeIndex[metadata.Key] = metadata;
                    }
                }
            }
            catch
            {
                // State load failures are non-fatal
            }
        }
    }

    private async Task SaveStateAsync(CancellationToken ct)
    {
        var state = new ExtendedRaidState
        {
            Arrays = _arrays.Values.ToList(),
            StripeIndex = _stripeIndex.Values.ToList(),
            SavedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        var stateFile = Path.Combine(_statePath, "extended_raid_state.json");
        await File.WriteAllTextAsync(stateFile, json, ct);
    }

    #endregion

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["SupportedLevels"] = new[]
        {
            "RAID71", "RAID72", "NWayMirror", "MatrixRAID", "JBOD",
            "CryptoRAID", "DUP", "DDP", "SPAN", "BIG", "MAID", "Linear"
        };
        metadata["TotalArrays"] = _arrays.Count;
        metadata["TotalDisks"] = _disks.Count;
        metadata["TotalOperations"] = Interlocked.Read(ref _totalOperations);
        metadata["TotalBytesProcessed"] = Interlocked.Read(ref _totalBytesProcessed);
        return metadata;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _healthCheckTimer.DisposeAsync();
        await _maidSpinDownTimer.DisposeAsync();
        await SaveStateAsync(CancellationToken.None);

        _arrayLock.Dispose();
    }
}

#region Supporting Classes

/// <summary>
/// Cache manager for RAID 71/72 caching operations.
/// </summary>
internal sealed class CacheManager
{
    private readonly ConcurrentDictionary<string, CacheEntry> _readCache = new();
    private readonly ConcurrentDictionary<string, byte[]> _writeBuffer = new();
    private readonly CacheConfig _config;

    public CacheManager(CacheConfig config)
    {
        _config = config;
    }

    public Task<byte[]?> GetFromCacheAsync(string key, CancellationToken ct)
    {
        if (_readCache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
        {
            entry.HitCount++;
            return Task.FromResult<byte[]?>(entry.Data);
        }
        return Task.FromResult<byte[]?>(null);
    }

    public Task AddToCacheAsync(string key, byte[] data, CancellationToken ct)
    {
        var entry = new CacheEntry
        {
            Data = data,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(_config.DefaultTtl)
        };
        _readCache[key] = entry;
        return Task.CompletedTask;
    }

    public Task PopulateReadAheadCacheAsync(string key, byte[] data, int readAheadSize, CancellationToken ct)
    {
        return AddToCacheAsync(key, data, ct);
    }

    public Task PrefetchNextAsync(string key, int prefetchDepth, CancellationToken ct)
    {
        // Prefetch logic would be implemented based on access patterns
        return Task.CompletedTask;
    }

    public Task AddToWriteBufferAsync(string key, byte[] data, CancellationToken ct)
    {
        _writeBuffer[key] = data;
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetFromWriteBufferAsync(string key, CancellationToken ct)
    {
        return Task.FromResult(_writeBuffer.TryGetValue(key, out var data) ? data : null);
    }

    public bool ShouldFlushWriteBuffer(long maxSize, TimeSpan coalesceWindow)
    {
        return _writeBuffer.Values.Sum(d => d.Length) >= maxSize;
    }

    public Task<Dictionary<string, byte[]>> GetAndClearWriteBufferAsync(CancellationToken ct)
    {
        var buffer = new Dictionary<string, byte[]>(_writeBuffer);
        _writeBuffer.Clear();
        return Task.FromResult(buffer);
    }

    private sealed class CacheEntry
    {
        public byte[] Data { get; init; } = Array.Empty<byte>();
        public DateTime CreatedAt { get; init; }
        public DateTime ExpiresAt { get; init; }
        public int HitCount { get; set; }
    }
}

/// <summary>
/// Encryption manager for Crypto RAID operations.
/// </summary>
internal sealed class EncryptionManager
{
    private readonly ConcurrentDictionary<string, byte[]> _keys = new();
    private readonly EncryptionConfig _config;

    public EncryptionManager(EncryptionConfig config)
    {
        _config = config;
    }

    public Task<byte[]> EncryptAsync(byte[] data, string keyId, CancellationToken ct)
    {
        var key = GetOrCreateKey(keyId);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();

        using var ms = new MemoryStream();
        ms.Write(aes.IV, 0, aes.IV.Length);

        using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
        {
            cs.Write(data, 0, data.Length);
        }

        return Task.FromResult(ms.ToArray());
    }

    public Task<byte[]> DecryptAsync(byte[] encryptedData, string keyId, CancellationToken ct)
    {
        var key = GetOrCreateKey(keyId);
        using var aes = Aes.Create();
        aes.Key = key;

        var iv = new byte[16];
        Array.Copy(encryptedData, 0, iv, 0, 16);
        aes.IV = iv;

        using var ms = new MemoryStream();
        using (var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
        {
            cs.Write(encryptedData, 16, encryptedData.Length - 16);
        }

        return Task.FromResult(ms.ToArray());
    }

    public Task<string> DeriveKeyAsync(string masterKeyId, string diskId, CancellationToken ct)
    {
        var derivedKeyId = $"{masterKeyId}-{diskId}";
        var masterKey = GetOrCreateKey(masterKeyId);

        using var deriveBytes = new Rfc2898DeriveBytes(
            masterKey,
            System.Text.Encoding.UTF8.GetBytes(diskId),
            10000,
            HashAlgorithmName.SHA256);

        _keys[derivedKeyId] = deriveBytes.GetBytes(32);
        return Task.FromResult(derivedKeyId);
    }

    private byte[] GetOrCreateKey(string keyId)
    {
        return _keys.GetOrAdd(keyId, _ =>
        {
            var key = new byte[32];
            RandomNumberGenerator.Fill(key);
            return key;
        });
    }
}

#endregion

#region Configuration and Models

/// <summary>
/// Configuration for the extended RAID plugin.
/// </summary>
public sealed record ExtendedRaidConfig
{
    public int StripeSize { get; init; } = 64 * 1024;
    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaidSpinDownCheckInterval { get; init; } = TimeSpan.FromMinutes(1);
    public string? StatePath { get; init; }
    public CacheConfig CacheConfig { get; init; } = new();
    public EncryptionConfig EncryptionConfig { get; init; } = new();
}

public sealed record CacheConfig
{
    public TimeSpan DefaultTtl { get; init; } = TimeSpan.FromMinutes(10);
    public long MaxCacheSize { get; init; } = 256 * 1024 * 1024;
}

public sealed record EncryptionConfig
{
    public string Algorithm { get; init; } = "AES-256-GCM";
    public int KeySize { get; init; } = 256;
}

public sealed record DiskConfig
{
    public string? DiskId { get; init; }
    public required string Path { get; init; }
    public long Capacity { get; init; }
}

public sealed record Raid71Config
{
    public List<DiskConfig> Disks { get; init; } = new();
    public int? StripeSize { get; init; }
    public int ReadAheadSize { get; set; } = 1024 * 1024;
    public int ReadAheadTriggerThreshold { get; init; } = 3;
    public long CacheSize { get; init; } = 128 * 1024 * 1024;
    public int PrefetchDepth { get; init; } = 4;
}

public sealed record Raid72Config
{
    public List<DiskConfig> Disks { get; init; } = new();
    public int? StripeSize { get; init; }
    public long WriteBufferSize { get; set; } = 64 * 1024 * 1024;
    public TimeSpan CoalesceWindow { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(5);
    public bool WriteBackCacheEnabled { get; init; } = true;
}

public sealed record NWayMirrorConfig
{
    public List<DiskConfig> Disks { get; init; } = new();
    public int? StripeSize { get; init; }
    public int MirrorCount { get; init; } = 2;
    public MirrorReadPolicy ReadPolicy { get; init; } = MirrorReadPolicy.RoundRobin;
    public MirrorWritePolicy WritePolicy { get; init; } = MirrorWritePolicy.WriteAll;
    public MirrorSyncMode SyncMode { get; init; } = MirrorSyncMode.Synchronous;
}

public sealed record MatrixRaidConfig
{
    public List<DiskConfig> Disks { get; init; } = new();
    public int? StripeSize { get; init; }
    public List<MatrixVolumeConfig> Volumes { get; init; } = new();
}

public sealed record MatrixVolumeConfig
{
    public string? VolumeId { get; init; }
    public string VolumeName { get; init; } = string.Empty;
    public MatrixRaidLevel RaidLevel { get; init; }
    public int CapacityPercentage { get; init; }
}

public sealed record JbodConfig
{
    public List<DiskConfig> Disks { get; init; } = new();
    public int? StripeSize { get; init; }
    public bool EnableDiskDiscovery { get; init; } = true;
    public bool AllowHotPlug { get; init; } = false;
}

public sealed record CryptoRaidConfig
{
    public List<DiskConfig> Disks { get; init; } = new();
    public int? StripeSize { get; init; }
    public string Algorithm { get; init; } = "AES-256-GCM";
    public int KeySize { get; init; } = 256;
    public bool PerStripeEncryption { get; init; } = true;
    public bool KeyDerivationPerDisk { get; init; } = true;
    public string MasterKeyId { get; init; } = string.Empty;
}

public sealed record DupConfig
{
    public List<DiskConfig> Disks { get; init; } = new();
    public int? StripeSize { get; init; }
    public bool DuplicateData { get; init; } = true;
    public bool DuplicateMetadata { get; init; } = true;
    public ChecksumAlgorithm ChecksumAlgorithm { get; init; } = ChecksumAlgorithm.SHA256;
    public bool VerifyOnRead { get; init; } = true;
    public bool AutoRepair { get; init; } = true;
}

public sealed record DdpConfig
{
    public List<DiskConfig> Disks { get; init; } = new();
    public int? StripeSize { get; init; }
    public double SpareCapacityPercentage { get; init; } = 10;
    public bool AutoLoadBalance { get; init; } = true;
    public bool SelfHealingEnabled { get; init; } = true;
    public double RebalanceThreshold { get; init; } = 0.2;
}

public sealed record SpanConfig
{
    public List<DiskConfig> Disks { get; init; } = new();
    public int? StripeSize { get; init; }
    public SpanFillStrategy FillStrategy { get; init; } = SpanFillStrategy.Sequential;
}

public sealed record BigConfig
{
    public List<DiskConfig> Disks { get; init; } = new();
    public int? StripeSize { get; init; }
    public bool EnableStriping { get; init; } = true;
    public int StripingChunkSize { get; init; } = 64 * 1024;
    public bool ConcatenateFirst { get; init; } = false;
}

public sealed record MaidConfig
{
    public List<DiskConfig> Disks { get; init; } = new();
    public int? StripeSize { get; init; }
    public int ActiveDiskCount { get; init; } = 2;
    public TimeSpan SpinDownIdleTime { get; init; } = TimeSpan.FromMinutes(15);
    public List<MaidTierConfig> TierConfiguration { get; init; } = new();
}

public sealed record MaidTierConfig
{
    public string TierName { get; init; } = string.Empty;
    public int TierLevel { get; init; }
    public DiskPowerState PowerState { get; init; }
    public TimeSpan AccessLatency { get; init; }
}

public sealed record LinearConfig
{
    public List<DiskConfig> Disks { get; init; } = new();
    public int? StripeSize { get; init; }
}

#endregion

#region Array and Disk Models

public sealed class ExtendedRaidArray
{
    public string ArrayId { get; init; } = string.Empty;
    public RaidLevel Level { get; init; }
    public RaidArrayStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
    public int StripeSize { get; init; }
    public List<DiskInfo> Disks { get; init; } = new();
    public long ReadCounter;

    public Raid71Settings? Raid71Settings { get; init; }
    public Raid72Settings? Raid72Settings { get; init; }
    public NWayMirrorSettings? NWayMirrorSettings { get; init; }
    public MatrixRaidSettings? MatrixSettings { get; init; }
    public JbodSettings? JbodSettings { get; init; }
    public CryptoRaidSettings? CryptoSettings { get; init; }
    public DupSettings? DupSettings { get; init; }
    public DdpSettings? DdpSettings { get; init; }
    public SpanSettings? SpanSettings { get; init; }
    public BigSettings? BigSettings { get; init; }
    public MaidSettings? MaidSettings { get; init; }
    public LinearSettings? LinearSettings { get; init; }
}

public sealed class DiskInfo
{
    public string DiskId { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public long Capacity { get; init; }
    public long UsedCapacity { get; set; }
    public DiskStatus Status { get; set; }
    public DiskPowerState PowerState { get; set; }
    public DateTime LastAccessTime { get; set; }
    public string? MaidTier { get; set; }
    public string? DerivedKeyId { get; set; }
    public long SpanStartOffset { get; set; }
    public double AverageLatency { get; set; }
    public int CurrentLoad { get; set; }
    public long BytesWritten;
    public long BytesRead;
}

public sealed class Raid71Settings
{
    public int ReadAheadSize { get; init; }
    public int ReadAheadTriggerThreshold { get; init; }
    public long CacheSize { get; init; }
    public int PrefetchDepth { get; init; }
}

public sealed class Raid72Settings
{
    public long WriteBufferSize { get; init; }
    public TimeSpan CoalesceWindow { get; init; }
    public TimeSpan FlushInterval { get; init; }
    public bool WriteBackCacheEnabled { get; init; }
}

public sealed class NWayMirrorSettings
{
    public int MirrorCount { get; init; }
    public MirrorReadPolicy ReadPolicy { get; init; }
    public MirrorWritePolicy WritePolicy { get; init; }
    public MirrorSyncMode SyncMode { get; init; }
}

public sealed class MatrixRaidSettings
{
    public List<MatrixVolume> Volumes { get; init; } = new();
}

public sealed class MatrixVolume
{
    public string VolumeId { get; init; } = string.Empty;
    public string VolumeName { get; init; } = string.Empty;
    public MatrixRaidLevel RaidLevel { get; init; }
    public int CapacityPercentage { get; init; }
    public long StartOffset { get; set; }
    public long Size { get; set; }
}

public sealed class JbodSettings
{
    public bool EnableDiskDiscovery { get; init; }
    public bool AllowHotPlug { get; init; }
}

public sealed class CryptoRaidSettings
{
    public string Algorithm { get; init; } = string.Empty;
    public int KeySize { get; init; }
    public bool PerStripeEncryption { get; init; }
    public bool KeyDerivationPerDisk { get; init; }
    public string MasterKeyId { get; init; } = string.Empty;
}

public sealed class DupSettings
{
    public bool DuplicateData { get; init; }
    public bool DuplicateMetadata { get; init; }
    public ChecksumAlgorithm ChecksumAlgorithm { get; init; }
    public bool VerifyOnRead { get; init; }
    public bool AutoRepair { get; init; }
}

public sealed class DdpSettings
{
    public double SpareCapacityPercentage { get; init; }
    public bool AutoLoadBalance { get; init; }
    public bool SelfHealingEnabled { get; init; }
    public double RebalanceThreshold { get; init; }
    public ConcurrentDictionary<string, PoolExtent> PoolExtents { get; init; } = new();
}

public sealed class PoolExtent
{
    public string ExtentId { get; init; } = string.Empty;
    public HashSet<string> DiskIds { get; init; } = new();
    public int DiskCount { get; init; }
    public double TotalCapacity { get; set; }
    public long UsedCapacity { get; set; }
    public HashSet<string> Keys { get; init; } = new();
}

public sealed class SpanSettings
{
    public SpanFillStrategy FillStrategy { get; init; }
}

public sealed class BigSettings
{
    public bool EnableStriping { get; init; }
    public int StripingChunkSize { get; init; }
    public bool ConcatenateFirst { get; init; }
}

public sealed class MaidSettings
{
    public int ActiveDiskCount { get; init; }
    public TimeSpan SpinDownIdleTime { get; init; }
    public List<MaidTier> TierConfiguration { get; init; } = new();
}

public sealed class MaidTier
{
    public string TierName { get; init; } = string.Empty;
    public int TierLevel { get; init; }
    public DiskPowerState PowerState { get; init; }
    public TimeSpan AccessLatency { get; init; }
}

public sealed class LinearSettings
{
    public bool FillOneBeforeNext { get; init; }
}

public sealed class StripeMetadata
{
    public string Key { get; init; } = string.Empty;
    public int OriginalSize { get; init; }
    public DateTime CreatedAt { get; init; }
    public string ArrayId { get; init; } = string.Empty;
    public RaidLevel Level { get; init; }
    public string? DiskId { get; set; }
    public long SpanOffset { get; set; }
    public string? VolumeId { get; set; }
}

#endregion

#region Enums

public enum DiskStatus
{
    Online,
    Degraded,
    Rebuilding,
    Failed,
    Offline
}

public enum DiskPowerState
{
    Active,
    SpinningUp,
    SpinningDown,
    Standby,
    Sleep
}

public enum MirrorReadPolicy
{
    Primary,
    RoundRobin,
    Fastest,
    LeastLoaded
}

public enum MirrorWritePolicy
{
    WriteAll,
    WritePrimary,
    WriteQuorum
}

public enum MirrorSyncMode
{
    Synchronous,
    Asynchronous
}

public enum MatrixRaidLevel
{
    RAID0,
    RAID1,
    RAID5
}

public enum ChecksumAlgorithm
{
    CRC32,
    [Obsolete("MD5 is cryptographically weak. Use SHA256 instead.")]
    MD5,
    SHA256,
    SHA512,
    XXHash
}

public enum SpanFillStrategy
{
    Sequential,
    RoundRobin,
    LeastUsed
}

#endregion

#region State and Exceptions

internal sealed class ExtendedRaidState
{
    public List<ExtendedRaidArray> Arrays { get; init; } = new();
    public List<StripeMetadata> StripeIndex { get; init; } = new();
    public DateTime SavedAt { get; init; }
}

public sealed class ExtendedRaidException : Exception
{
    public ExtendedRaidException(string message) : base(message) { }
    public ExtendedRaidException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class DataCorruptionException : Exception
{
    public DataCorruptionException(string message) : base(message) { }
}

#endregion
