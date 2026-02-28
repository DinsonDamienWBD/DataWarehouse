using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Blockchain
{
    public class PolygonConnectionStrategy : BlockchainConnectionStrategyBase
    {
        public override string StrategyId => "polygon";
        public override string DisplayName => "Polygon";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Polygon blockchain (EVM-compatible)";
        public override string[] Tags => new[] { "polygon", "blockchain", "evm", "web3", "layer2" };
        public PolygonConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct) { var client = new HttpClient { BaseAddress = new Uri(config.ConnectionString) }; await client.PostAsync("/", new StringContent(@"{""jsonrpc"":""2.0"",""method"":""eth_blockNumber"",""params"":[],""id"":1}", Encoding.UTF8, "application/json"), ct); return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "Polygon JSON-RPC" }); }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            try { using var r = await handle.GetConnection<HttpClient>().PostAsync("/", new StringContent(@"{""jsonrpc"":""2.0"",""method"":""eth_blockNumber"",""params"":[],""id"":1}", Encoding.UTF8, "application/json"), ct); return r.IsSuccessStatusCode; }
            catch { return false; }
        }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>().Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "Polygon node reachable" : "Polygon node unreachable", sw.Elapsed, DateTimeOffset.UtcNow);
        }
        public override Task<string> GetBlockAsync(IConnectionHandle handle, string blockIdentifier, CancellationToken ct = default) => throw new NotSupportedException("Requires Web3 library");
        public override Task<string> SubmitTransactionAsync(IConnectionHandle handle, string signedTransaction, CancellationToken ct = default) => throw new NotSupportedException("Requires Web3 library");
    }
}
