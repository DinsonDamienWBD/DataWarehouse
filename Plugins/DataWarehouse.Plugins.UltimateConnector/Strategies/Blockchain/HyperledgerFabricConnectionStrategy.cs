using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Blockchain
{
    public class HyperledgerFabricConnectionStrategy : BlockchainConnectionStrategyBase
    {
        public override string StrategyId => "hyperledger-fabric";
        public override string DisplayName => "Hyperledger Fabric";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Hyperledger Fabric blockchain network";
        public override string[] Tags => new[] { "hyperledger", "fabric", "blockchain", "enterprise", "grpc" };
        public HyperledgerFabricConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct) { var client = new HttpClient { BaseAddress = new Uri(config.ConnectionString) }; await client.GetAsync("/", ct); return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "Hyperledger Fabric" }); }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            try { using var r = await handle.GetConnection<HttpClient>().GetAsync("/healthz", ct); return r.IsSuccessStatusCode; }
            catch { return false; }
        }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>().Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "Fabric peer reachable" : "Fabric peer unreachable", sw.Elapsed, DateTimeOffset.UtcNow);
        }
        public override Task<string> GetBlockAsync(IConnectionHandle handle, string blockIdentifier, CancellationToken ct = default) => throw new NotSupportedException("Requires Fabric SDK");
        public override Task<string> SubmitTransactionAsync(IConnectionHandle handle, string signedTransaction, CancellationToken ct = default) => throw new NotSupportedException("Requires Fabric SDK");
    }
}
