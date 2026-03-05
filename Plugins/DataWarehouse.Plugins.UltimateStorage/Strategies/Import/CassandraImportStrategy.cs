using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Import
{
    /// <summary>Cassandra bulk import strategy using CQL COPY or batch inserts.</summary>
    public class CassandraImportStrategy : UltimateStorageStrategyBase
    {
        public override string StrategyId => "cassandra-import";
        public override string Name => "Cassandra Import";
        public override bool IsProductionReady => false;
        public override StorageTier Tier => StorageTier.Warm;

        public override StorageCapabilities Capabilities => new StorageCapabilities { SupportsMetadata = true, SupportsStreaming = true, ConsistencyModel = ConsistencyModel.Eventual };

        protected override Task InitializeCoreAsync(CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires CassandraCSharpDriver NuGet package. Add a reference to CassandraCSharpDriver " +
                "(DataStax C# Driver for Apache Cassandra) and implement a real ISession using " +
                "Cluster.Builder().AddContactPoint().Build().Connect() with proper keyspace and table configuration.");
        }

        protected override Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires CassandraCSharpDriver NuGet package. See InitializeCoreAsync for details.");
        }

        protected override Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires CassandraCSharpDriver NuGet package. See InitializeCoreAsync for details.");
        }

        protected override Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires CassandraCSharpDriver NuGet package. See InitializeCoreAsync for details.");
        }

        protected override Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires CassandraCSharpDriver NuGet package. See InitializeCoreAsync for details.");
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires CassandraCSharpDriver NuGet package. See InitializeCoreAsync for details.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        protected override Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires CassandraCSharpDriver NuGet package. See InitializeCoreAsync for details.");
        }

        protected override Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct) => Task.FromResult(new StorageHealthInfo { Status = HealthStatus.Unhealthy, Message = "Cassandra client not configured. Requires CassandraCSharpDriver.", CheckedAt = DateTime.UtcNow });
        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct) => Task.FromResult<long?>(null);
        protected override int GetMaxKeyLength() => 1024;
    }
}
