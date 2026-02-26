using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Import
{
    /// <summary>BigQuery bulk import strategy using load jobs or streaming inserts.</summary>
    public class BigQueryImportStrategy : UltimateStorageStrategyBase
    {
        public override string StrategyId => "bigquery-import";
        public override string Name => "BigQuery Import";
        public override bool IsProductionReady => false;
        public override StorageTier Tier => StorageTier.Warm;

        public override StorageCapabilities Capabilities => new StorageCapabilities { SupportsMetadata = true, SupportsStreaming = true, ConsistencyModel = ConsistencyModel.Strong };

        protected override Task InitializeCoreAsync(CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires Google.Cloud.BigQuery.V2 NuGet package. Add a reference to Google.Cloud.BigQuery.V2 " +
                "and implement real BigQueryClient load jobs (BigQueryClient.CreateLoadJobAsync) or " +
                "streaming inserts (BigQueryClient.InsertRowsAsync) with service account credentials.");
        }

        protected override Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires Google.Cloud.BigQuery.V2 NuGet package. See InitializeCoreAsync for details.");
        }

        protected override Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires Google.Cloud.BigQuery.V2 NuGet package. See InitializeCoreAsync for details.");
        }

        protected override Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires Google.Cloud.BigQuery.V2 NuGet package. See InitializeCoreAsync for details.");
        }

        protected override Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires Google.Cloud.BigQuery.V2 NuGet package. See InitializeCoreAsync for details.");
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires Google.Cloud.BigQuery.V2 NuGet package. See InitializeCoreAsync for details.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        protected override Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires Google.Cloud.BigQuery.V2 NuGet package. See InitializeCoreAsync for details.");
        }

        protected override Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct) => Task.FromResult(new StorageHealthInfo { Status = HealthStatus.Unhealthy, Message = "BigQuery client not configured. Requires Google.Cloud.BigQuery.V2.", CheckedAt = DateTime.UtcNow });
        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct) => Task.FromResult<long?>(null);
        protected override int GetMaxKeyLength() => 1024;
    }
}
