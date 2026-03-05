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
        public override Task<(bool IsValid, string[] Errors)> ValidateHl7Async(IConnectionHandle handle, string hl7Message, CancellationToken ct = default)
        {
            var errors = new List<string>();

            // CDA is XML-based HL7 Clinical Document Architecture
            if (string.IsNullOrWhiteSpace(hl7Message))
            {
                errors.Add("CDA document cannot be empty");
                return Task.FromResult((false, errors.ToArray()));
            }

            // Basic XML/CDA structure validation
            if (!hl7Message.TrimStart().StartsWith("<?xml") && !hl7Message.TrimStart().StartsWith("<ClinicalDocument"))
            {
                errors.Add("CDA document must be valid XML starting with XML declaration or ClinicalDocument element");
            }

            if (!hl7Message.Contains("ClinicalDocument"))
            {
                errors.Add("CDA document must contain ClinicalDocument root element");
            }

            if (!hl7Message.Contains("urn:hl7-org:v3"))
            {
                errors.Add("Warning: CDA document should reference urn:hl7-org:v3 namespace");
            }

            return Task.FromResult((errors.Count == 0, errors.ToArray()));
        }

        public override async Task<string> QueryFhirAsync(IConnectionHandle handle, string resourceType, string? query = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            // CDA documents can be queried via document repository
            var url = string.IsNullOrEmpty(query) ? $"/cda/{resourceType}" : $"/cda/{resourceType}?{query}";
            try
            {
                using var response = await client.GetAsync(url, ct);
 response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex)
            {
                return $"{{\"error\":\"{ex.Message}\"}}";
            }
        }
    }
}
