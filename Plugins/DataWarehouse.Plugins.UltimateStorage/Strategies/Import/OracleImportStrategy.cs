using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Import
{
    /// <summary>Oracle bulk import strategy using SQL*Loader or External Tables for high-performance data ingestion.</summary>
    public class OracleImportStrategy : UltimateStorageStrategyBase
    {
        public override string StrategyId => "oracle-import";
        public override string Name => "Oracle Bulk Import";
        public override bool IsProductionReady => false;
        public override StorageTier Tier => StorageTier.Warm;

        public override StorageCapabilities Capabilities => new StorageCapabilities { SupportsMetadata = true, SupportsStreaming = true, ConsistencyModel = ConsistencyModel.Strong };

        protected override Task InitializeCoreAsync(CancellationToken ct) => Task.CompletedTask;

        protected override Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
            => throw new NotSupportedException($"{Name} (strategy '{StrategyId}') is not production-ready. Configure a supported storage backend.");

        protected override Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
            => throw new NotSupportedException($"{Name} (strategy '{StrategyId}') is not production-ready. Configure a supported storage backend.");

        protected override Task DeleteAsyncCore(string key, CancellationToken ct)
            => throw new NotSupportedException($"{Name} (strategy '{StrategyId}') is not production-ready. Configure a supported storage backend.");

        protected override Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
            => throw new NotSupportedException($"{Name} (strategy '{StrategyId}') is not production-ready. Configure a supported storage backend.");

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            throw new NotSupportedException($"{Name} (strategy '{StrategyId}') is not production-ready. Configure a supported storage backend.");
            #pragma warning disable CS0162
            yield break;
            #pragma warning restore CS0162
        }

        protected override Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
            => throw new NotSupportedException($"{Name} (strategy '{StrategyId}') is not production-ready. Configure a supported storage backend.");

        protected override Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct) => Task.FromResult(new StorageHealthInfo { Status = HealthStatus.Degraded, Message = "Strategy not production-ready", CheckedAt = DateTime.UtcNow });
        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct) => Task.FromResult<long?>(null);
        protected override int GetMaxKeyLength() => 1024;
    }
}
