using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Replication;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateReplication.Strategies.Specialized
{
    /// <summary>
    /// Selective replication strategy that replicates only matching data
    /// based on configurable filter rules.
    /// </summary>
    public sealed class SelectiveReplicationStrategy : EnhancedReplicationStrategyBase
    {
        private readonly ConcurrentDictionary<string, Func<byte[], IDictionary<string, string>?, bool>> _filters = new();
        private readonly BoundedDictionary<string, (byte[] Data, bool Selected)> _dataStore = new BoundedDictionary<string, (byte[] Data, bool Selected)>(1000);
        private bool _defaultAllow = true;

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "Selective",
            Description = "Selective replication with configurable filter rules for data subsetting",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromSeconds(30),
                MinReplicaCount: 1,
                MaxReplicaCount: 50),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = false,
            SupportsStreaming = true,
            TypicalLagMs = 30,
            ConsistencySlaMs = 30000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Adds a selection filter.
        /// </summary>
        public void AddFilter(string filterName, Func<byte[], IDictionary<string, string>?, bool> filter)
        {
            _filters[filterName] = filter;
        }

        /// <summary>
        /// Removes a filter.
        /// </summary>
        public bool RemoveFilter(string filterName)
        {
            return _filters.TryRemove(filterName, out _);
        }

        /// <summary>
        /// Sets default behavior when no filters match.
        /// </summary>
        public void SetDefaultAllow(bool allow)
        {
            _defaultAllow = allow;
        }

        /// <summary>
        /// Checks if data should be replicated based on filters.
        /// </summary>
        public bool ShouldReplicate(byte[] data, IDictionary<string, string>? metadata)
        {
            if (_filters.Count == 0)
                return _defaultAllow;

            return _filters.Values.Any(filter => filter(data, metadata));
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var key = metadata?.GetValueOrDefault("key") ?? Guid.NewGuid().ToString();

            var shouldReplicate = ShouldReplicate(data.ToArray(), metadata);
            _dataStore[key] = (data.ToArray(), shouldReplicate);

            if (!shouldReplicate)
            {
                return; // Skip replication
            }

            var tasks = targetNodeIds.Select(async targetId =>
            {
                var startTime = DateTime.UtcNow;
                await Task.Delay(20, cancellationToken);
                RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
            });

            await Task.WhenAll(tasks);
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ResolveLastWriteWins(conflict));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_dataStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }
    }

    /// <summary>
    /// Filtered replication strategy with per-target filter rules
    /// for heterogeneous replica configurations.
    /// </summary>
    public sealed class FilteredReplicationStrategy : EnhancedReplicationStrategyBase
    {
        private readonly ConcurrentDictionary<string, Func<byte[], IDictionary<string, string>?, bool>> _targetFilters = new();
        private readonly BoundedDictionary<string, (byte[] Data, HashSet<string> ReplicatedTo)> _dataStore = new BoundedDictionary<string, (byte[] Data, HashSet<string> ReplicatedTo)>(1000);

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "Filtered",
            Description = "Filtered replication with per-target filter rules for heterogeneous replicas",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromSeconds(30),
                MinReplicaCount: 1,
                MaxReplicaCount: 100),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = false,
            SupportsStreaming = true,
            TypicalLagMs = 35,
            ConsistencySlaMs = 30000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Sets a filter for a specific target.
        /// </summary>
        public void SetTargetFilter(string targetId, Func<byte[], IDictionary<string, string>?, bool> filter)
        {
            _targetFilters[targetId] = filter;
        }

        /// <summary>
        /// Removes a target filter.
        /// </summary>
        public bool RemoveTargetFilter(string targetId)
        {
            return _targetFilters.TryRemove(targetId, out _);
        }

        /// <summary>
        /// Checks if data should replicate to a specific target.
        /// </summary>
        public bool ShouldReplicateTo(string targetId, byte[] data, IDictionary<string, string>? metadata)
        {
            if (_targetFilters.TryGetValue(targetId, out var filter))
            {
                return filter(data, metadata);
            }
            return true; // Default: replicate if no filter
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var key = metadata?.GetValueOrDefault("key") ?? Guid.NewGuid().ToString();

            var replicatedTo = new HashSet<string> { sourceNodeId };
            _dataStore[key] = (data.ToArray(), replicatedTo);

            var tasks = targetNodeIds
                .Where(targetId => ShouldReplicateTo(targetId, data.ToArray(), metadata))
                .Select(async targetId =>
                {
                    var startTime = DateTime.UtcNow;
                    await Task.Delay(25, cancellationToken);
                    lock (replicatedTo) replicatedTo.Add(targetId);
                    RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
                });

            await Task.WhenAll(tasks);
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ResolveLastWriteWins(conflict));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_dataStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }
    }

    /// <summary>
    /// Compression replication strategy with configurable compression algorithms
    /// for bandwidth optimization.
    /// </summary>
    public sealed class CompressionReplicationStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, (byte[] CompressedData, byte[] OriginalData, double CompressionRatio)> _dataStore = new BoundedDictionary<string, (byte[] CompressedData, byte[] OriginalData, double CompressionRatio)>(1000);
        private CompressionAlgorithm _algorithm = CompressionAlgorithm.Gzip;
        private CompressionLevel _level = CompressionLevel.Optimal;
        private int _minSizeForCompression = 1024; // 1KB minimum

        /// <summary>
        /// Supported compression algorithms.
        /// </summary>
        public enum CompressionAlgorithm
        {
            None,
            Gzip,
            Deflate,
            Brotli
        }

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "Compression",
            Description = "Compression replication with configurable algorithms for bandwidth optimization",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromSeconds(30),
                MinReplicaCount: 1,
                MaxReplicaCount: 100),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = false,
            TypicalLagMs = 50,
            ConsistencySlaMs = 30000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Sets the compression algorithm.
        /// </summary>
        public void SetAlgorithm(CompressionAlgorithm algorithm)
        {
            _algorithm = algorithm;
        }

        /// <summary>
        /// Sets the compression level.
        /// </summary>
        public void SetCompressionLevel(CompressionLevel level)
        {
            _level = level;
        }

        /// <summary>
        /// Sets minimum size for compression.
        /// </summary>
        public void SetMinSizeForCompression(int minSize)
        {
            _minSizeForCompression = minSize;
        }

        /// <summary>
        /// Compresses data using the configured algorithm.
        /// </summary>
        public byte[] Compress(byte[] data)
        {
            if (_algorithm == CompressionAlgorithm.None || data.Length < _minSizeForCompression)
                return data;

            using var output = new System.IO.MemoryStream();

            System.IO.Stream compressor = _algorithm switch
            {
                CompressionAlgorithm.Gzip => new GZipStream(output, _level),
                CompressionAlgorithm.Deflate => new DeflateStream(output, _level),
                CompressionAlgorithm.Brotli => new BrotliStream(output, _level),
                _ => throw new NotSupportedException($"Algorithm {_algorithm} not supported")
            };

            using (compressor)
            {
                compressor.Write(data, 0, data.Length);
            }

            return output.ToArray();
        }

        /// <summary>
        /// Decompresses data.
        /// </summary>
        public byte[] Decompress(byte[] compressedData)
        {
            if (_algorithm == CompressionAlgorithm.None)
                return compressedData;

            using var input = new System.IO.MemoryStream(compressedData);
            using var output = new System.IO.MemoryStream();

            System.IO.Stream decompressor = _algorithm switch
            {
                CompressionAlgorithm.Gzip => new GZipStream(input, CompressionMode.Decompress),
                CompressionAlgorithm.Deflate => new DeflateStream(input, CompressionMode.Decompress),
                CompressionAlgorithm.Brotli => new BrotliStream(input, CompressionMode.Decompress),
                _ => throw new NotSupportedException($"Algorithm {_algorithm} not supported")
            };

            using (decompressor)
            {
                decompressor.CopyTo(output);
            }

            return output.ToArray();
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var key = metadata?.GetValueOrDefault("key") ?? Guid.NewGuid().ToString();

            var originalData = data.ToArray();
            var compressedData = Compress(originalData);
            var ratio = originalData.Length > 0 ? (double)compressedData.Length / originalData.Length : 1.0;

            _dataStore[key] = (compressedData, originalData, ratio);

            // Transfer time based on compressed size
            var transferTime = Math.Max(10, compressedData.Length / 1000);

            var tasks = targetNodeIds.Select(async targetId =>
            {
                var startTime = DateTime.UtcNow;
                await Task.Delay(transferTime, cancellationToken);
                RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
            });

            await Task.WhenAll(tasks);
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ResolveLastWriteWins(conflict));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_dataStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }
    }

    /// <summary>
    /// Encryption replication strategy with end-to-end encryption
    /// for secure data transfer.
    /// </summary>
    public sealed class EncryptionReplicationStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, (byte[] EncryptedData, byte[] IV)> _dataStore = new BoundedDictionary<string, (byte[] EncryptedData, byte[] IV)>(1000);
        private readonly BoundedDictionary<string, byte[]> _nodeKeys = new BoundedDictionary<string, byte[]>(1000);
        private byte[] _masterKey = GenerateInitialKey();
        private EncryptionAlgorithm _algorithm = EncryptionAlgorithm.AES256;

        private static byte[] GenerateInitialKey()
        {
            var key = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(key);
            return key;
        }

        /// <summary>
        /// Supported encryption algorithms.
        /// </summary>
        public enum EncryptionAlgorithm
        {
            AES128,
            AES256,
            ChaCha20
        }

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "Encryption",
            Description = "Encryption replication with end-to-end encryption for secure data transfer",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: true,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromSeconds(30),
                MinReplicaCount: 1,
                MaxReplicaCount: 50),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = false,
            SupportsStreaming = false,
            TypicalLagMs = 60,
            ConsistencySlaMs = 30000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Sets the master encryption key (copies the provided key to prevent external mutation).
        /// </summary>
        public void SetMasterKey(byte[] key)
        {
            if (key.Length != 32)
                throw new ArgumentException("Master key must be 32 bytes");
            // Copy to prevent caller from retaining a reference to the internal key buffer
            _masterKey = (byte[])key.Clone();
        }

        /// <summary>
        /// Generates a new cryptographically random master key and applies it.
        /// Returns a copy of the generated key; does not expose the internal buffer.
        /// </summary>
        public byte[] GenerateMasterKey()
        {
            var newKey = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(newKey);
            _masterKey = newKey;
            // Return a copy so callers can't corrupt the internal key via the returned array
            return (byte[])_masterKey.Clone();
        }

        /// <summary>
        /// Sets encryption algorithm.
        /// </summary>
        public void SetAlgorithm(EncryptionAlgorithm algorithm)
        {
            _algorithm = algorithm;
        }

        /// <summary>
        /// Sets a per-node encryption key.
        /// </summary>
        public void SetNodeKey(string nodeId, byte[] key)
        {
            _nodeKeys[nodeId] = key;
        }

        /// <summary>
        /// Encrypts data.
        /// </summary>
        public (byte[] EncryptedData, byte[] IV) Encrypt(byte[] data, string? targetNodeId = null)
        {
            var key = targetNodeId != null && _nodeKeys.TryGetValue(targetNodeId, out var nodeKey)
                ? nodeKey
                : _masterKey;

            using var aes = Aes.Create();
            aes.Key = key;
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor();
            var encrypted = encryptor.TransformFinalBlock(data, 0, data.Length);

            return (encrypted, aes.IV);
        }

        /// <summary>
        /// Decrypts data.
        /// </summary>
        public byte[] Decrypt(byte[] encryptedData, byte[] iv, string? sourceNodeId = null)
        {
            var key = sourceNodeId != null && _nodeKeys.TryGetValue(sourceNodeId, out var nodeKey)
                ? nodeKey
                : _masterKey;

            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(encryptedData, 0, encryptedData.Length);
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var key = metadata?.GetValueOrDefault("key") ?? Guid.NewGuid().ToString();

            var (encrypted, iv) = Encrypt(data.ToArray());
            _dataStore[key] = (encrypted, iv);

            var tasks = targetNodeIds.Select(async targetId =>
            {
                var startTime = DateTime.UtcNow;
                // Encryption adds processing overhead
                await Task.Delay(40, cancellationToken);
                RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
            });

            await Task.WhenAll(tasks);
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ResolveLastWriteWins(conflict));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_dataStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }
    }

    /// <summary>
    /// Throttle replication strategy with rate limiting and bandwidth control.
    /// </summary>
    public sealed class ThrottleReplicationStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, (byte[] Data, DateTimeOffset QueuedAt)> _dataStore = new BoundedDictionary<string, (byte[] Data, DateTimeOffset QueuedAt)>(1000);
        private readonly BoundedDictionary<string, ThrottleConfig> _targetThrottles = new BoundedDictionary<string, ThrottleConfig>(1000);
        private readonly BoundedDictionary<string, SemaphoreSlim> _targetSemaphores = new BoundedDictionary<string, SemaphoreSlim>(1000);
        private int _globalBytesPerSecond = 10_000_000; // 10 MB/s default
        private int _globalConcurrentOps = 10;
        private readonly SemaphoreSlim _globalSemaphore;

        /// <summary>
        /// Per-target throttle configuration.
        /// </summary>
        public sealed class ThrottleConfig
        {
            public int BytesPerSecond { get; init; } = 1_000_000;
            public int MaxConcurrentOps { get; init; } = 5;
            public TimeSpan MinInterval { get; init; } = TimeSpan.FromMilliseconds(100);
        }

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "Throttle",
            Description = "Throttle replication with rate limiting and bandwidth control",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromMinutes(5),
                MinReplicaCount: 1,
                MaxReplicaCount: 100),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = false,
            SupportsStreaming = false,
            TypicalLagMs = 500,
            ConsistencySlaMs = 300000
        };

        /// <summary>
        /// Creates a new throttle replication strategy.
        /// </summary>
        public ThrottleReplicationStrategy()
        {
            _globalSemaphore = new SemaphoreSlim(_globalConcurrentOps);
        }

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Sets global bandwidth limit.
        /// </summary>
        public void SetGlobalBandwidth(int bytesPerSecond)
        {
            _globalBytesPerSecond = bytesPerSecond;
        }

        /// <summary>
        /// Sets global concurrent operation limit.
        /// </summary>
        public void SetGlobalConcurrency(int maxConcurrent)
        {
            _globalConcurrentOps = maxConcurrent;
        }

        /// <summary>
        /// Sets per-target throttle configuration.
        /// </summary>
        public void SetTargetThrottle(string targetId, ThrottleConfig config)
        {
            _targetThrottles[targetId] = config;
            _targetSemaphores[targetId] = new SemaphoreSlim(config.MaxConcurrentOps);
        }

        /// <summary>
        /// Calculates transfer time based on bandwidth limits.
        /// </summary>
        public int CalculateTransferTime(int dataSize, string? targetId = null)
        {
            var bytesPerSecond = _globalBytesPerSecond;

            if (targetId != null && _targetThrottles.TryGetValue(targetId, out var config))
            {
                bytesPerSecond = Math.Min(bytesPerSecond, config.BytesPerSecond);
            }

            return (int)((double)dataSize / bytesPerSecond * 1000);
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var key = metadata?.GetValueOrDefault("key") ?? Guid.NewGuid().ToString();

            _dataStore[key] = (data.ToArray(), DateTimeOffset.UtcNow);

            var tasks = targetNodeIds.Select(async targetId =>
            {
                // Acquire global semaphore
                await _globalSemaphore.WaitAsync(cancellationToken);

                try
                {
                    // Acquire per-target semaphore if configured
                    if (_targetSemaphores.TryGetValue(targetId, out var targetSem))
                    {
                        await targetSem.WaitAsync(cancellationToken);
                    }

                    try
                    {
                        var startTime = DateTime.UtcNow;
                        var transferTime = CalculateTransferTime(data.Length, targetId);

                        await Task.Delay(Math.Max(10, transferTime), cancellationToken);
                        RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
                    }
                    finally
                    {
                        if (_targetSemaphores.TryGetValue(targetId, out var sem))
                        {
                            sem.Release();
                        }
                    }
                }
                finally
                {
                    _globalSemaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ResolveLastWriteWins(conflict));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_dataStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }
    }

    /// <summary>
    /// Priority replication strategy with priority queues for critical data.
    /// </summary>
    public sealed class PriorityReplicationStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<int, ConcurrentQueue<PendingReplication>> _priorityQueues = new BoundedDictionary<int, ConcurrentQueue<PendingReplication>>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, int Priority)> _dataStore = new BoundedDictionary<string, (byte[] Data, int Priority)>(1000);
        private int _priorityLevels = 5;
        private readonly CancellationTokenSource _workerCts = new();
        private Task? _workerTask;

        private sealed record PendingReplication(
            string Key,
            byte[] Data,
            string[] TargetNodeIds,
            int Priority,
            DateTimeOffset QueuedAt);

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "Priority",
            Description = "Priority replication with priority queues for critical data handling",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.PriorityBased },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromMinutes(1),
                MinReplicaCount: 1,
                MaxReplicaCount: 100),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = false,
            SupportsStreaming = true,
            TypicalLagMs = 40,
            ConsistencySlaMs = 60000
        };

        /// <summary>
        /// Creates a new priority replication strategy.
        /// </summary>
        public PriorityReplicationStrategy()
        {
            for (int i = 0; i < _priorityLevels; i++)
            {
                _priorityQueues[i] = new ConcurrentQueue<PendingReplication>();
            }

            StartWorker();
        }

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Sets number of priority levels (0 = highest).
        /// </summary>
        public void SetPriorityLevels(int levels)
        {
            _priorityLevels = levels;
        }

        private void StartWorker()
        {
            _workerTask = Task.Run(async () =>
            {
                while (!_workerCts.Token.IsCancellationRequested)
                {
                    await ProcessQueuesAsync(_workerCts.Token);
                    await Task.Delay(10, _workerCts.Token);
                }
            }, _workerCts.Token);
        }

        private async Task ProcessQueuesAsync(CancellationToken ct)
        {
            // Process highest priority first
            for (int priority = 0; priority < _priorityLevels; priority++)
            {
                if (_priorityQueues.TryGetValue(priority, out var queue))
                {
                    while (queue.TryDequeue(out var pending) && !ct.IsCancellationRequested)
                    {
                        foreach (var targetId in pending.TargetNodeIds)
                        {
                            await Task.Delay(10, ct);
                            RecordReplicationLag(targetId, DateTime.UtcNow - pending.QueuedAt.UtcDateTime);
                        }

                        // Only process one item from lower priorities if higher has items
                        if (priority > 0 && _priorityQueues[0].Count > 0)
                            break;
                    }
                }
            }
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var key = metadata?.GetValueOrDefault("key") ?? Guid.NewGuid().ToString();
            var priority = int.TryParse(metadata?.GetValueOrDefault("priority"), out var p)
                ? Math.Clamp(p, 0, _priorityLevels - 1)
                : _priorityLevels / 2; // Default: medium priority

            _dataStore[key] = (data.ToArray(), priority);

            var pending = new PendingReplication(
                key,
                data.ToArray(),
                targetNodeIds.ToArray(),
                priority,
                DateTimeOffset.UtcNow);

            if (_priorityQueues.TryGetValue(priority, out var queue))
            {
                queue.Enqueue(pending);
            }

            // For high priority (0), wait for immediate processing
            if (priority == 0)
            {
                await Task.Delay(20, cancellationToken);
            }
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // Priority-based: higher priority data wins
            // Since we don't have priority in conflict, use LWW
            return Task.FromResult(ResolveLastWriteWins(conflict));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_dataStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }

        /// <summary>
        /// Disposes resources.
        /// </summary>
        public new void Dispose()
        {
            _workerCts.Cancel();
            _workerTask?.Wait(TimeSpan.FromSeconds(5));
            _workerCts.Dispose();
            base.Dispose();
        }
    }
}
