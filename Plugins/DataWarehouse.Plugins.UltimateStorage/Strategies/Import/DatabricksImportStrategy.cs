using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Import
{
    /// <summary>Databricks bulk import strategy using Delta Lake and Auto Loader.</summary>
    public class DatabricksImportStrategy : UltimateStorageStrategyBase
    {
        public override string StrategyId => "databricks-import";
        public override string Name => "Databricks Import";
        public override bool IsProductionReady => false;
        public override StorageTier Tier => StorageTier.Warm;

        public override StorageCapabilities Capabilities => new StorageCapabilities { SupportsMetadata = true, SupportsStreaming = true, SupportsVersioning = true, ConsistencyModel = ConsistencyModel.Strong };

        protected override Task InitializeCoreAsync(CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires Databricks SDK (Microsoft.Azure.Databricks.Client or Databricks.SDK). " +
                "Add the Databricks SDK NuGet package and implement real DatabricksClient / WorkspaceClient " +
                "with cluster configuration, Delta Lake table paths, and Auto Loader stream definitions.");
        }

        protected override Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires Databricks SDK. See InitializeCoreAsync for details.");
        }

        protected override Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires Databricks SDK. See InitializeCoreAsync for details.");
        }

        protected override Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires Databricks SDK. See InitializeCoreAsync for details.");
        }

        protected override Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires Databricks SDK. See InitializeCoreAsync for details.");
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires Databricks SDK. See InitializeCoreAsync for details.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        protected override Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires Databricks SDK. See InitializeCoreAsync for details.");
        }

        protected override Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct) => Task.FromResult(new StorageHealthInfo { Status = HealthStatus.Unhealthy, Message = "Databricks client not configured. Requires Databricks SDK.", CheckedAt = DateTime.UtcNow });
        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct) => Task.FromResult<long?>(null);
        protected override int GetMaxKeyLength() => 1024;
    }
}
