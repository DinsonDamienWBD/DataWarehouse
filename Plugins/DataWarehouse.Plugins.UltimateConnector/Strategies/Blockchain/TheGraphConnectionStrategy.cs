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
    public class TheGraphConnectionStrategy : BlockchainConnectionStrategyBase
    {
        public override string StrategyId => "thegraph";
        public override string DisplayName => "The Graph";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to The Graph protocol for blockchain data indexing";
        public override string[] Tags => new[] { "thegraph", "graphql", "indexing", "blockchain", "web3" };
        public TheGraphConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var client = new HttpClient { BaseAddress = new Uri(config.ConnectionString) };
            // Valid GraphQL introspection query to verify subgraph connectivity
            var body = new StringContent("{\"query\":\"{_meta{block{number}}}\"}", Encoding.UTF8, "application/json");
            await client.PostAsync("", body, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "The Graph GraphQL" });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            try { using var r = await handle.GetConnection<HttpClient>().PostAsync("", new StringContent("{\"query\":\"{_meta{block{number}}}\"}", Encoding.UTF8, "application/json"), ct); return r.IsSuccessStatusCode; }
            catch { return false; }
        }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>().Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "The Graph subgraph reachable" : "The Graph subgraph unreachable", sw.Elapsed, DateTimeOffset.UtcNow);
        }
        public override Task<string> GetBlockAsync(IConnectionHandle handle, string blockIdentifier, CancellationToken ct = default) => throw new NotSupportedException("Use GraphQL queries");
        public override Task<string> SubmitTransactionAsync(IConnectionHandle handle, string signedTransaction, CancellationToken ct = default) => throw new NotSupportedException("The Graph is read-only");
    }
}
