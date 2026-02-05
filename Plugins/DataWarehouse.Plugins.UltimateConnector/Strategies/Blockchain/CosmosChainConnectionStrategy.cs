using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Blockchain
{
    public class CosmosChainConnectionStrategy : BlockchainConnectionStrategyBase
    {
        public override string StrategyId => "cosmos-chain";
        public override string DisplayName => "Cosmos Chain";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Cosmos SDK-based blockchains";
        public override string[] Tags => new[] { "cosmos", "blockchain", "ibc", "tendermint", "rest" };
        public CosmosChainConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct) { var client = new HttpClient { BaseAddress = new Uri(config.ConnectionString) }; await client.GetAsync("/node_info", ct); return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "Cosmos REST" }); }
        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(true);
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>().Dispose(); return Task.CompletedTask; }
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(true, "Cosmos node", TimeSpan.Zero, DateTimeOffset.UtcNow));
        public override Task<string> GetBlockAsync(IConnectionHandle handle, string blockIdentifier, CancellationToken ct = default) => throw new NotSupportedException("Requires Cosmos SDK");
        public override Task<string> SubmitTransactionAsync(IConnectionHandle handle, string signedTransaction, CancellationToken ct = default) => throw new NotSupportedException("Requires Cosmos SDK");
    }
}
