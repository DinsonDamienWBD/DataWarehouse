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
    public class AvalancheConnectionStrategy : BlockchainConnectionStrategyBase
    {
        public override string StrategyId => "avalanche";
        public override string DisplayName => "Avalanche";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Avalanche blockchain (EVM C-Chain)";
        public override string[] Tags => new[] { "avalanche", "blockchain", "evm", "web3", "avax" };
        public AvalancheConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct) { var client = new HttpClient { BaseAddress = new Uri(config.ConnectionString ?? throw new ArgumentException("Connection string is required")) }; await client.PostAsync("/ext/bc/C/rpc", new StringContent(@"{""jsonrpc"":""2.0"",""method"":""eth_blockNumber"",""params"":[],""id"":1}", Encoding.UTF8, "application/json"), ct); return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "Avalanche JSON-RPC" }); }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            try { using var r = await handle.GetConnection<HttpClient>().PostAsync("/ext/bc/C/rpc", new StringContent(@"{""jsonrpc"":""2.0"",""method"":""eth_blockNumber"",""params"":[],""id"":1}", Encoding.UTF8, "application/json"), ct); return r.IsSuccessStatusCode; }
            catch (OperationCanceledException) { throw; } catch { return false; }
        }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>().Dispose(); if (handle is DefaultConnectionHandle dh) dh.MarkDisconnected(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "Avalanche node reachable" : "Avalanche node unreachable", sw.Elapsed, DateTimeOffset.UtcNow);
        }
        public override async Task<string> GetBlockAsync(IConnectionHandle handle, string blockIdentifier, CancellationToken ct = default) { var client = handle.GetConnection<HttpClient>(); using var response = await client.PostAsync("/ext/bc/C/rpc", new StringContent($@"{{""jsonrpc"":""2.0"",""method"":""eth_getBlockByNumber"",""params"":[""{blockIdentifier}"",true],""id"":1}}", Encoding.UTF8, "application/json"), ct); response.EnsureSuccessStatusCode(); return await response.Content.ReadAsStringAsync(ct); }
        public override async Task<string> SubmitTransactionAsync(IConnectionHandle handle, string signedTransaction, CancellationToken ct = default) { var client = handle.GetConnection<HttpClient>(); using var response = await client.PostAsync("/ext/bc/C/rpc", new StringContent($@"{{""jsonrpc"":""2.0"",""method"":""eth_sendRawTransaction"",""params"":[""{signedTransaction}""],""id"":1}}", Encoding.UTF8, "application/json"), ct); response.EnsureSuccessStatusCode(); return await response.Content.ReadAsStringAsync(ct); }
    }
}
