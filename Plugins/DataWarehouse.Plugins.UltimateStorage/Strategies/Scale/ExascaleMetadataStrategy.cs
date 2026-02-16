using DataWarehouse.Plugins.UltimateStorage.Strategies.Scale.LsmTree;
using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Scale
{
    /// <summary>
    /// Exabyte-scale metadata management strategy using LSM-Tree storage engine.
    /// Provides high-performance key-value storage with write-ahead logging and background compaction.
    /// </summary>
    public class ExascaleMetadataStrategy : UltimateStorageStrategyBase
    {
        private LsmTreeEngine? _lsmTree;
        private readonly Lazy<string> _dataDirectory;

        /// <summary>
        /// Initializes a new instance of the ExascaleMetadataStrategy.
        /// </summary>
        public ExascaleMetadataStrategy()
        {
            _dataDirectory = new Lazy<string>(() =>
            {
                var tempPath = Path.GetTempPath();
                var enginePath = Path.Combine(tempPath, "DataWarehouse", "ExascaleMetadata", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(enginePath);
                return enginePath;
            });
        }

        public override string StrategyId => "exascale-metadata";
        public override string Name => "Exascale Metadata Strategy";
        public override StorageTier Tier => StorageTier.Hot;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsVersioning = true,
            ConsistencyModel = ConsistencyModel.ReadAfterWrite
        };

        /// <summary>
        /// Initializes the LSM-Tree engine.
        /// </summary>
        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            var options = new LsmTreeOptions
            {
                MemTableMaxSize = 4 * 1024 * 1024,
                Level0CompactionThreshold = 4,
                MaxLevels = 7,
                BlockSize = 4096,
                EnableBackgroundCompaction = true
            };

            _lsmTree = new LsmTreeEngine(_dataDirectory.Value, options);
            await _lsmTree.InitializeAsync(ct);
        }

        /// <summary>
        /// Stores data and metadata in the LSM-Tree.
        /// </summary>
        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.Store);

            if (_lsmTree == null)
            {
                throw new InvalidOperationException("Strategy not initialized");
            }

            // Read data into memory
            using var ms = new MemoryStream();
            await data.CopyToAsync(ms, ct);
            var dataBytes = ms.ToArray();

            IncrementBytesStored(dataBytes.Length);

            // Encode key as UTF8
            var keyBytes = Encoding.UTF8.GetBytes(key);

            // Store data
            await _lsmTree.PutAsync(keyBytes, dataBytes, ct);

            // Store metadata if provided
            if (metadata != null && metadata.Count > 0)
            {
                var metadataKey = Encoding.UTF8.GetBytes($"{key}:metadata");
                var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
                var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);
                await _lsmTree.PutAsync(metadataKey, metadataBytes, ct);
            }

            return new StorageObjectMetadata
            {
                Key = key,
                Size = dataBytes.Length,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                Tier = Tier,
                CustomMetadata = metadata as IReadOnlyDictionary<string, string>
            };
        }

        /// <summary>
        /// Retrieves data from the LSM-Tree.
        /// </summary>
        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.Retrieve);

            if (_lsmTree == null)
            {
                throw new InvalidOperationException("Strategy not initialized");
            }

            var keyBytes = Encoding.UTF8.GetBytes(key);
            var dataBytes = await _lsmTree.GetAsync(keyBytes, ct);

            if (dataBytes == null)
            {
                throw new FileNotFoundException($"Key not found: {key}");
            }

            IncrementBytesRetrieved(dataBytes.Length);

            return new MemoryStream(dataBytes);
        }

        /// <summary>
        /// Deletes a key from the LSM-Tree.
        /// </summary>
        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.Delete);

            if (_lsmTree == null)
            {
                throw new InvalidOperationException("Strategy not initialized");
            }

            var keyBytes = Encoding.UTF8.GetBytes(key);
            await _lsmTree.DeleteAsync(keyBytes, ct);

            // Also delete metadata
            var metadataKey = Encoding.UTF8.GetBytes($"{key}:metadata");
            await _lsmTree.DeleteAsync(metadataKey, ct);
        }

        /// <summary>
        /// Checks if a key exists in the LSM-Tree.
        /// </summary>
        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.Exists);

            if (_lsmTree == null)
            {
                throw new InvalidOperationException("Strategy not initialized");
            }

            var keyBytes = Encoding.UTF8.GetBytes(key);
            var dataBytes = await _lsmTree.GetAsync(keyBytes, ct);

            return dataBytes != null;
        }

        /// <summary>
        /// Lists all keys matching the given prefix.
        /// </summary>
        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(
            string? prefix,
            [EnumeratorCancellation] CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.List);

            if (_lsmTree == null)
            {
                throw new InvalidOperationException("Strategy not initialized");
            }

            var prefixBytes = string.IsNullOrEmpty(prefix)
                ? Array.Empty<byte>()
                : Encoding.UTF8.GetBytes(prefix);

            await foreach (var kvp in _lsmTree.ScanAsync(prefixBytes, ct))
            {
                var key = Encoding.UTF8.GetString(kvp.Key);

                // Skip metadata entries
                if (key.EndsWith(":metadata"))
                {
                    continue;
                }

                yield return new StorageObjectMetadata
                {
                    Key = key,
                    Size = kvp.Value.Length,
                    Created = DateTime.UtcNow,
                    Modified = DateTime.UtcNow,
                    Tier = Tier
                };
            }
        }

        /// <summary>
        /// Gets metadata for a given key.
        /// </summary>
        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.GetMetadata);

            if (_lsmTree == null)
            {
                throw new InvalidOperationException("Strategy not initialized");
            }

            var keyBytes = Encoding.UTF8.GetBytes(key);
            var dataBytes = await _lsmTree.GetAsync(keyBytes, ct);

            if (dataBytes == null)
            {
                throw new FileNotFoundException($"Key not found: {key}");
            }

            // Try to get metadata
            IDictionary<string, string>? metadata = null;
            var metadataKey = Encoding.UTF8.GetBytes($"{key}:metadata");
            var metadataBytes = await _lsmTree.GetAsync(metadataKey, ct);

            if (metadataBytes != null)
            {
                var metadataJson = Encoding.UTF8.GetString(metadataBytes);
                metadata = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson);
            }

            return new StorageObjectMetadata
            {
                Key = key,
                Size = dataBytes.Length,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                Tier = Tier,
                CustomMetadata = metadata as IReadOnlyDictionary<string, string>
            };
        }

        /// <summary>
        /// Gets health status of the storage strategy.
        /// </summary>
        protected override Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            var status = _lsmTree != null ? HealthStatus.Healthy : HealthStatus.Degraded;
            var message = _lsmTree != null
                ? "Exascale metadata with LSM-Tree storage engine"
                : "LSM-Tree not initialized";

            return Task.FromResult(new StorageHealthInfo
            {
                Status = status,
                Message = message,
                CheckedAt = DateTime.UtcNow
            });
        }

        /// <summary>
        /// Gets available capacity (unlimited for LSM-Tree).
        /// </summary>
        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            return Task.FromResult<long?>(null); // Unlimited
        }

        /// <summary>
        /// Gets the maximum key length.
        /// </summary>
        protected override int GetMaxKeyLength() => 8192;

        /// <summary>
        /// Disposes the LSM-Tree engine.
        /// </summary>
        protected override async ValueTask DisposeCoreAsync()
        {
            if (_lsmTree != null)
            {
                await _lsmTree.DisposeAsync();
                _lsmTree = null;
            }

            await base.DisposeCoreAsync();
        }
    }
}
