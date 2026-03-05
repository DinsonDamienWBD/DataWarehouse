using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.FileSystem
{
    public sealed class HdfsConnectionStrategy : ConnectionStrategyBase
    {
        public override string StrategyId => "hdfs";
        public override string DisplayName => "HDFS";
        public override ConnectorCategory Category => ConnectorCategory.FileSystem;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Hadoop HDFS via WebHDFS REST API";
        public override string[] Tags => new[] { "hdfs", "hadoop", "filesystem", "distributed", "bigdata" };

        public HdfsConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = (config.ConnectionString ?? throw new ArgumentException("HDFS connection string required")).Split(':');
            var client = new HttpClient { BaseAddress = new Uri($"http://{parts[0]}:{(parts.Length > 1 ? parts[1] : "9870")}") };
            try
            {
                await client.GetAsync("/webhdfs/v1/?op=GETFILESTATUS", ct);
                return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "WebHDFS" });
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { var response = await handle.GetConnection<HttpClient>().GetAsync("/webhdfs/v1/?op=GETFILESTATUS", ct); return response.IsSuccessStatusCode; }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>().Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, "HDFS namenode", sw.Elapsed, DateTimeOffset.UtcNow); }
    }
}
