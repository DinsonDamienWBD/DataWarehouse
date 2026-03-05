using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Import
{
    /// <summary>MongoDB bulk import strategy using mongoimport or bulk write API.</summary>
    public class MongoImportStrategy : UltimateStorageStrategyBase
    {
        public override string StrategyId => "mongo-import";
        public override string Name => "MongoDB Import";
        public override bool IsProductionReady => false;
        public override StorageTier Tier => StorageTier.Warm;

        public override StorageCapabilities Capabilities => new StorageCapabilities { SupportsMetadata = true, SupportsStreaming = true, ConsistencyModel = ConsistencyModel.Eventual };

        protected override Task InitializeCoreAsync(CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires MongoDB.Driver NuGet package. Add a reference to MongoDB.Driver and " +
                "implement a real MongoClient with IMongoDatabase / IMongoCollection<BsonDocument> " +
                "and bulk write operations (BulkWriteAsync with InsertOneModel<T>).");
        }

        protected override Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires MongoDB.Driver NuGet package. See InitializeCoreAsync for details.");
        }

        protected override Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires MongoDB.Driver NuGet package. See InitializeCoreAsync for details.");
        }

        protected override Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires MongoDB.Driver NuGet package. See InitializeCoreAsync for details.");
        }

        protected override Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires MongoDB.Driver NuGet package. See InitializeCoreAsync for details.");
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires MongoDB.Driver NuGet package. See InitializeCoreAsync for details.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        protected override Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires MongoDB.Driver NuGet package. See InitializeCoreAsync for details.");
        }

        protected override Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct) => Task.FromResult(new StorageHealthInfo { Status = HealthStatus.Unhealthy, Message = "MongoDB client not configured. Requires MongoDB.Driver.", CheckedAt = DateTime.UtcNow });
        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct) => Task.FromResult<long?>(null);
        protected override int GetMaxKeyLength() => 1024;
    }
}
