using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Healthcare
{
    public class CdaConnectionStrategy : HealthcareConnectionStrategyBase
    {
        public override string StrategyId => "cda";
        public override string DisplayName => "CDA";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to CDA (Clinical Document Architecture) systems";
        public override string[] Tags => new[] { "cda", "healthcare", "hl7", "documents", "xml" };

        public CdaConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var client = new HttpClient { BaseAddress = new Uri(config.ConnectionString) };
            await client.GetAsync("/", ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "CDA/HTTP" });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { var response = await handle.GetConnection<HttpClient>().GetAsync("/", ct); return response.IsSuccessStatusCode; }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>().Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, "CDA endpoint", sw.Elapsed, DateTimeOffset.UtcNow); }
        public override Task<(bool IsValid, string[] Errors)> ValidateHl7Async(IConnectionHandle handle, string hl7Message, CancellationToken ct = default) => throw new NotSupportedException("Requires CDA parser");
        public override Task<string> QueryFhirAsync(IConnectionHandle handle, string resourceType, string? query = null, CancellationToken ct = default) => throw new NotSupportedException("CDA does not use FHIR");
    }
}
