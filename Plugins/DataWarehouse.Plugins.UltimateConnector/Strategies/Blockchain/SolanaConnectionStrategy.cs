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
    public class SolanaConnectionStrategy : BlockchainConnectionStrategyBase
    {
        public override string StrategyId => "solana";
        public override string DisplayName => "Solana";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Solana blockchain via JSON-RPC";
        public override string[] Tags => new[] { "solana", "blockchain", "web3", "jsonrpc", "spl" };

        public SolanaConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var client = new HttpClient { BaseAddress = new Uri(config.ConnectionString ?? throw new ArgumentException("Connection string is required")) };
            using var response = await client.PostAsync("/", new StringContent(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""getHealth""}", Encoding.UTF8, "application/json"), ct);
            response.EnsureSuccessStatusCode();
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "Solana JSON-RPC" });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { var response = await handle.GetConnection<HttpClient>().PostAsync("/", new StringContent(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""getHealth""}", Encoding.UTF8, "application/json"), ct); return response.IsSuccessStatusCode; }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>().Dispose(); if (handle is DefaultConnectionHandle dh) dh.MarkDisconnected(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, "Solana node", sw.Elapsed, DateTimeOffset.UtcNow); }
        public override async Task<string> GetBlockAsync(IConnectionHandle handle, string blockIdentifier, CancellationToken ct = default) { var client = handle.GetConnection<HttpClient>(); using var response = await client.PostAsync("/", new StringContent($@"{{""jsonrpc"":""2.0"",""id"":1,""method"":""getBlock"",""params"":[{blockIdentifier}]}}", Encoding.UTF8, "application/json"), ct); response.EnsureSuccessStatusCode(); return await response.Content.ReadAsStringAsync(ct); }
        public override async Task<string> SubmitTransactionAsync(IConnectionHandle handle, string signedTransaction, CancellationToken ct = default) { var client = handle.GetConnection<HttpClient>(); using var response = await client.PostAsync("/", new StringContent($@"{{""jsonrpc"":""2.0"",""id"":1,""method"":""sendTransaction"",""params"":[""{signedTransaction}""]}}", Encoding.UTF8, "application/json"), ct); response.EnsureSuccessStatusCode(); return await response.Content.ReadAsStringAsync(ct); }
    }
}
