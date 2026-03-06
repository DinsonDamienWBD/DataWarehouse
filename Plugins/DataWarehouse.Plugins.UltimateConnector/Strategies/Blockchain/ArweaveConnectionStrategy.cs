using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Blockchain
{
    public class ArweaveConnectionStrategy : BlockchainConnectionStrategyBase
    {
        public override string StrategyId => "arweave";
        public override string DisplayName => "Arweave";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Arweave permanent storage blockchain";
        public override string[] Tags => new[] { "arweave", "storage", "blockchain", "permanent", "web3" };
        public ArweaveConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct) { var endpoint = config.ConnectionString ?? "https://arweave.net"; ArgumentException.ThrowIfNullOrWhiteSpace(endpoint, nameof(config.ConnectionString)); var client = new HttpClient { BaseAddress = new Uri(endpoint) }; await client.GetAsync("/info", ct); return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "Arweave HTTP" }); }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            try { using var response = await handle.GetConnection<HttpClient>().GetAsync("/info", ct); return response.IsSuccessStatusCode; }
            catch (OperationCanceledException) { throw; } catch { return false; }
        }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>().Dispose(); if (handle is DefaultConnectionHandle dh) dh.MarkDisconnected(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "Arweave node reachable" : "Arweave node unreachable", sw.Elapsed, DateTimeOffset.UtcNow);
        }
        public override async Task<string> GetBlockAsync(IConnectionHandle handle, string blockIdentifier, CancellationToken ct = default) { var client = handle.GetConnection<HttpClient>(); using var response = await client.GetAsync($"/block/hash/{blockIdentifier}", ct); response.EnsureSuccessStatusCode(); return await response.Content.ReadAsStringAsync(ct); }
        public override async Task<string> SubmitTransactionAsync(IConnectionHandle handle, string signedTransaction, CancellationToken ct = default) { var client = handle.GetConnection<HttpClient>(); using var response = await client.PostAsync("/tx", new StringContent(signedTransaction, System.Text.Encoding.UTF8, "application/json"), ct); response.EnsureSuccessStatusCode(); return await response.Content.ReadAsStringAsync(ct); }
    }
}
