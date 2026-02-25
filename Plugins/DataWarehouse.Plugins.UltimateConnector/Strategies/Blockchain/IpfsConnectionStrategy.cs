using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Blockchain
{
    public class IpfsConnectionStrategy : BlockchainConnectionStrategyBase
    {
        public override string StrategyId => "ipfs";
        public override string DisplayName => "IPFS";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to IPFS distributed file storage network";
        public override string[] Tags => new[] { "ipfs", "storage", "distributed", "web3", "p2p" };
        public IpfsConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct) { var parts = config.ConnectionString.Split(':'); var client = new HttpClient { BaseAddress = new Uri($"http://{parts[0]}:{(parts.Length > 1 ? parts[1] : "5001")}") }; await client.PostAsync("/api/v0/id", null, ct); return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "IPFS HTTP API" }); }
        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(true);
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>().Dispose(); return Task.CompletedTask; }
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(true, "IPFS node", TimeSpan.Zero, DateTimeOffset.UtcNow));
        public override Task<string> GetBlockAsync(IConnectionHandle handle, string blockIdentifier, CancellationToken ct = default) => throw new NotSupportedException("IPFS uses content addressing");
        public override Task<string> SubmitTransactionAsync(IConnectionHandle handle, string signedTransaction, CancellationToken ct = default) => throw new NotSupportedException("IPFS does not support transactions");
    }
}
