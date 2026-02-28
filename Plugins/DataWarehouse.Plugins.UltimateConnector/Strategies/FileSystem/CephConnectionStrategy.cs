using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.FileSystem
{
    public class CephConnectionStrategy : ConnectionStrategyBase
    {
        public override string StrategyId => "ceph";
        public override string DisplayName => "Ceph";
        public override ConnectorCategory Category => ConnectorCategory.FileSystem;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Ceph distributed storage (S3-compatible or native)";
        public override string[] Tags => new[] { "ceph", "storage", "distributed", "s3", "objectstore" };

        public CephConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var client = new HttpClient { BaseAddress = new Uri(config.ConnectionString) };
            try
            {
                await client.GetAsync("/", ct);
                return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "Ceph S3" });
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { var response = await handle.GetConnection<HttpClient>().GetAsync("/", ct); return response.IsSuccessStatusCode; }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>().Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, "Ceph cluster", sw.Elapsed, DateTimeOffset.UtcNow); }
    }
}
