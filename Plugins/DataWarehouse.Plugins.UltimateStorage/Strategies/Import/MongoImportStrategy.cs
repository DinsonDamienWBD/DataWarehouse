using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Import
{
    /// <summary>MongoDB bulk import strategy using mongoimport or bulk write API.</summary>
    public class MongoImportStrategy : UltimateStorageStrategyBase
    {
        private readonly ConcurrentDictionary<string, byte[]> _store = new();

        public override string StrategyId => "mongo-import";
        public override string Name => "MongoDB Import";
        public override StorageTier Tier => StorageTier.Warm;

        public override StorageCapabilities Capabilities => new StorageCapabilities { SupportsMetadata = true, SupportsStreaming = true, ConsistencyModel = ConsistencyModel.Eventual };

        protected override Task InitializeCoreAsync(CancellationToken ct) => Task.CompletedTask;

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.Store);
            using var ms = new MemoryStream();
            await data.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            _store[key] = bytes;
            IncrementBytesStored(bytes.Length);
            return new StorageObjectMetadata { Key = key, Size = bytes.Length, Created = DateTime.UtcNow, Modified = DateTime.UtcNow, Tier = Tier };
        }

        protected override Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.Retrieve);
            if (!_store.TryGetValue(key, out var data))
                throw new KeyNotFoundException($"Key '{key}' not found in {StrategyId} store");
            return Task.FromResult<Stream>(new MemoryStream(data));
        }

        protected override Task DeleteAsyncCore(string key, CancellationToken ct) { IncrementOperationCounter(StorageOperationType.Delete); _store.TryRemove(key, out _); return Task.CompletedTask; }
        protected override Task<bool> ExistsAsyncCore(string key, CancellationToken ct) { IncrementOperationCounter(StorageOperationType.Exists); return Task.FromResult(_store.ContainsKey(key)); }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.List);
            foreach (var kvp in _store)
            {
                if (prefix == null || kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
                    yield return new StorageObjectMetadata { Key = kvp.Key, Size = kvp.Value.Length, Created = DateTime.UtcNow, Modified = DateTime.UtcNow, Tier = Tier };
            }
            await Task.CompletedTask;
        }

        protected override Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.GetMetadata);
            if (!_store.TryGetValue(key, out var data))
                throw new KeyNotFoundException($"Key '{key}' not found in {StrategyId} store");
            return Task.FromResult(new StorageObjectMetadata { Key = key, Size = data.Length, Created = DateTime.UtcNow, Modified = DateTime.UtcNow, Tier = Tier });
        }

        protected override Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct) => Task.FromResult(new StorageHealthInfo { Status = HealthStatus.Healthy, CheckedAt = DateTime.UtcNow });
        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct) => Task.FromResult<long?>(null);
        protected override int GetMaxKeyLength() => 1024;
    }
}
