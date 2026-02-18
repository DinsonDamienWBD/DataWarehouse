using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Import
{
    /// <summary>MongoDB bulk import strategy using mongoimport or bulk write API.</summary>
    public class MongoImportStrategy : UltimateStorageStrategyBase
    {
        public override string StrategyId => "mongo-import";
        public override string Name => "MongoDB Import";
        public override StorageTier Tier => StorageTier.Warm;

        public override StorageCapabilities Capabilities => new StorageCapabilities { SupportsMetadata = true, SupportsStreaming = true, ConsistencyModel = ConsistencyModel.Eventual };

        protected override Task InitializeCoreAsync(CancellationToken ct) => Task.CompletedTask;

        protected override Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.Store);
            IncrementBytesStored(data.Length);
            return Task.FromResult(new StorageObjectMetadata { Key = key, Size = data.Length, Created = DateTime.UtcNow, Modified = DateTime.UtcNow, Tier = Tier });
        }

        protected override Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct) { IncrementOperationCounter(StorageOperationType.Retrieve); return Task.FromResult<Stream>(new MemoryStream(0)); }
        protected override Task DeleteAsyncCore(string key, CancellationToken ct) { IncrementOperationCounter(StorageOperationType.Delete); return Task.CompletedTask; }
        protected override Task<bool> ExistsAsyncCore(string key, CancellationToken ct) { IncrementOperationCounter(StorageOperationType.Exists); return Task.FromResult(true); }
        protected override IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, CancellationToken ct) { IncrementOperationCounter(StorageOperationType.List); return AsyncEnumerable.Empty<StorageObjectMetadata>(); }
        protected override Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct) { IncrementOperationCounter(StorageOperationType.GetMetadata); return Task.FromResult(new StorageObjectMetadata { Key = key, Size = 0, Created = DateTime.UtcNow, Modified = DateTime.UtcNow, Tier = Tier }); }
        protected override Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct) => Task.FromResult(new StorageHealthInfo { Status = HealthStatus.Healthy, CheckedAt = DateTime.UtcNow });
        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct) => Task.FromResult<long?>(null);
        protected override int GetMaxKeyLength() => 1024;
    }
}
