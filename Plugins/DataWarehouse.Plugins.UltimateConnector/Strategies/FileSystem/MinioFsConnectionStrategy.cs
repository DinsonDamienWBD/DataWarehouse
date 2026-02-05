using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.FileSystem
{
    public class MinioFsConnectionStrategy : ConnectionStrategyBase
    {
        public override string StrategyId => "minio-fs";
        public override string DisplayName => "MinIO";
        public override ConnectorCategory Category => ConnectorCategory.FileSystem;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to MinIO object storage (S3-compatible)";
        public override string[] Tags => new[] { "minio", "s3", "objectstore", "storage", "cloud" };

        public MinioFsConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var client = new HttpClient { BaseAddress = new Uri(config.ConnectionString) };
            var response = await client.GetAsync("/minio/health/live", ct);
            response.EnsureSuccessStatusCode();
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "MinIO S3" });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { var response = await handle.GetConnection<HttpClient>().GetAsync("/minio/health/live", ct); return response.IsSuccessStatusCode; }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>().Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, "MinIO server", sw.Elapsed, DateTimeOffset.UtcNow); }
    }
}
