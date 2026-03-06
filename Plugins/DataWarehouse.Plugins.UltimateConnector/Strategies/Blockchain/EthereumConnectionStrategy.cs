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
    public class EthereumConnectionStrategy : BlockchainConnectionStrategyBase
    {
        public override string StrategyId => "ethereum";
        public override string DisplayName => "Ethereum";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Ethereum blockchain via JSON-RPC";
        public override string[] Tags => new[] { "ethereum", "blockchain", "web3", "evm", "jsonrpc" };

        public EthereumConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var connStr = config.ConnectionString ?? throw new ArgumentException("Connection string is required");
            // Finding 244: Use configurable scheme — default to https for security
            var useSsl = !connStr.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
            var sanitized = connStr.Replace("https://", "").Replace("http://", "");
            var parts = sanitized.Split(':');
            var scheme = useSsl ? "https" : "http";
            var client = new HttpClient { BaseAddress = new Uri($"{scheme}://{parts[0]}:{(parts.Length > 1 ? parts[1] : "8545")}") };
            var rpcRequest = @"{""jsonrpc"":""2.0"",""method"":""eth_blockNumber"",""params"":[],""id"":1}";
            using var response = await client.PostAsync("/", new StringContent(rpcRequest, Encoding.UTF8, "application/json"), ct);
            response.EnsureSuccessStatusCode();
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "Ethereum JSON-RPC" });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { var client = handle.GetConnection<HttpClient>(); var response = await client.PostAsync("/", new StringContent(@"{""jsonrpc"":""2.0"",""method"":""eth_blockNumber"",""params"":[],""id"":1}", Encoding.UTF8, "application/json"), ct); return response.IsSuccessStatusCode; }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>().Dispose(); if (handle is DefaultConnectionHandle dh) dh.MarkDisconnected(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, "Ethereum node", sw.Elapsed, DateTimeOffset.UtcNow); }
        public override async Task<string> GetBlockAsync(IConnectionHandle handle, string blockIdentifier, CancellationToken ct = default) { var client = handle.GetConnection<HttpClient>(); using var response = await client.PostAsync("/", new StringContent($@"{{""jsonrpc"":""2.0"",""method"":""eth_getBlockByNumber"",""params"":[""{blockIdentifier}"",true],""id"":1}}", Encoding.UTF8, "application/json"), ct); response.EnsureSuccessStatusCode(); return await response.Content.ReadAsStringAsync(ct); }
        public override async Task<string> SubmitTransactionAsync(IConnectionHandle handle, string signedTransaction, CancellationToken ct = default) { var client = handle.GetConnection<HttpClient>(); using var response = await client.PostAsync("/", new StringContent($@"{{""jsonrpc"":""2.0"",""method"":""eth_sendRawTransaction"",""params"":[""{signedTransaction}""],""id"":1}}", Encoding.UTF8, "application/json"), ct); response.EnsureSuccessStatusCode(); return await response.Content.ReadAsStringAsync(ct); }
    }
}
