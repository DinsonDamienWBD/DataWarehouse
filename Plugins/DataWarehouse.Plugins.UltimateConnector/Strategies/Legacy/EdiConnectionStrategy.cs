using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Legacy
{
    public class EdiConnectionStrategy : LegacyConnectionStrategyBase
    {
        public override string StrategyId => "edi";
        public override string DisplayName => "EDI";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to EDI document exchange systems";
        public override string[] Tags => new[] { "edi", "legacy", "b2b", "x12", "edifact" };

        public EdiConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var client = new HttpClient { BaseAddress = new Uri(config.ConnectionString) };
            await client.GetAsync("/", ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "EDI/HTTP" });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { var response = await handle.GetConnection<HttpClient>().GetAsync("/", ct); return response.IsSuccessStatusCode; }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>().Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, "EDI endpoint", sw.Elapsed, DateTimeOffset.UtcNow); }
        public override Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default) => throw new NotSupportedException("Requires EDI parser");
        public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default) => throw new NotSupportedException("Requires EDI parser");
    }
}
