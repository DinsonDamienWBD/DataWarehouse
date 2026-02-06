using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Healthcare
{
    public class FhirR4ConnectionStrategy : HealthcareConnectionStrategyBase
    {
        public override string StrategyId => "fhir-r4";
        public override string DisplayName => "FHIR R4";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to FHIR R4 healthcare data exchange servers";
        public override string[] Tags => new[] { "fhir", "healthcare", "rest", "interoperability", "r4" };

        public FhirR4ConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var client = new HttpClient { BaseAddress = new Uri(config.ConnectionString) };
            var response = await client.GetAsync("/metadata", ct);
            response.EnsureSuccessStatusCode();
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "FHIR R4" });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { var response = await handle.GetConnection<HttpClient>().GetAsync("/metadata", ct); return response.IsSuccessStatusCode; }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>().Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, "FHIR R4 server", sw.Elapsed, DateTimeOffset.UtcNow); }
        public override Task<(bool IsValid, string[] Errors)> ValidateHl7Async(IConnectionHandle handle, string hl7Message, CancellationToken ct = default)
        {
            // FHIR R4 does not use HL7 v2 format - provide guidance
            return Task.FromResult((false, new[] { "FHIR R4 uses JSON/XML format, not HL7 v2. Use Hl7v2ConnectionStrategy for HL7 validation." }));
        }
        public override async Task<string> QueryFhirAsync(IConnectionHandle handle, string resourceType, string? query = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var url = string.IsNullOrEmpty(query) ? $"/{resourceType}" : $"/{resourceType}?{query}";
            var response = await client.GetAsync(url, ct);
            return await response.Content.ReadAsStringAsync(ct);
        }
    }
}
