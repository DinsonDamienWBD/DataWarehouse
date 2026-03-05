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
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct) { var parts = (config.ConnectionString ?? throw new ArgumentException("Connection string is required")).Split(':'); var client = new HttpClient { BaseAddress = new Uri($"https://{parts[0]}:{(parts.Length > 1 ? parts[1] : "5001")}") }; await client.PostAsync("/api/v0/id", null, ct); return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "IPFS HTTP API" }); }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            try { using var r = await handle.GetConnection<HttpClient>().PostAsync("/api/v0/id", null, ct); return r.IsSuccessStatusCode; }
            catch { return false; }
        }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>().Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "IPFS node reachable" : "IPFS node unreachable", sw.Elapsed, DateTimeOffset.UtcNow);
        }
        public override Task<string> GetBlockAsync(IConnectionHandle handle, string blockIdentifier, CancellationToken ct = default) => throw new NotSupportedException("IPFS uses content addressing");
        public override Task<string> SubmitTransactionAsync(IConnectionHandle handle, string signedTransaction, CancellationToken ct = default) => throw new NotSupportedException("IPFS does not support transactions");
    }
}
