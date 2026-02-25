using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Healthcare
{
    public class X12ConnectionStrategy : HealthcareConnectionStrategyBase
    {
        public override string StrategyId => "x12";
        public override string DisplayName => "X12 Healthcare EDI";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to X12 EDI systems for healthcare claims and transactions";
        public override string[] Tags => new[] { "x12", "healthcare", "edi", "claims", "transactions" };

        public X12ConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var client = new HttpClient { BaseAddress = new Uri(config.ConnectionString) };
            await client.GetAsync("/", ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "X12/HTTP" });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { var response = await handle.GetConnection<HttpClient>().GetAsync("/", ct); return response.IsSuccessStatusCode; }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>().Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, "X12 endpoint", sw.Elapsed, DateTimeOffset.UtcNow); }
        public override Task<(bool IsValid, string[] Errors)> ValidateHl7Async(IConnectionHandle handle, string hl7Message, CancellationToken ct = default)
        {
            var errors = new List<string>();

            // X12 EDI format validation
            if (string.IsNullOrWhiteSpace(hl7Message))
            {
                errors.Add("X12 document cannot be empty");
                return Task.FromResult((false, errors.ToArray()));
            }

            // Basic X12 structure validation
            if (!hl7Message.Contains("ISA*"))
            {
                errors.Add("X12 document must contain ISA interchange header");
            }

            if (!hl7Message.Contains("IEA*"))
            {
                errors.Add("X12 document must contain IEA interchange trailer");
            }

            if (!hl7Message.Contains("GS*") || !hl7Message.Contains("GE*"))
            {
                errors.Add("Warning: X12 document should contain GS/GE functional group envelope");
            }

            // Check for common healthcare transaction sets
            var hasHealthcareTxn = hl7Message.Contains("ST*837") || // Claims
                                   hl7Message.Contains("ST*835") || // Remittance
                                   hl7Message.Contains("ST*270") || // Eligibility Inquiry
                                   hl7Message.Contains("ST*271") || // Eligibility Response
                                   hl7Message.Contains("ST*276") || // Claim Status Inquiry
                                   hl7Message.Contains("ST*277");   // Claim Status Response

            if (!hasHealthcareTxn)
            {
                errors.Add("Note: Document does not contain standard healthcare transaction sets (837/835/270/271/276/277)");
            }

            return Task.FromResult((errors.Count == 0, errors.ToArray()));
        }

        public override async Task<string> QueryFhirAsync(IConnectionHandle handle, string resourceType, string? query = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            // X12 can be queried via clearinghouse APIs
            var url = string.IsNullOrEmpty(query) ? $"/x12/{resourceType}" : $"/x12/{resourceType}?{query}";
            try
            {
                var response = await client.GetAsync(url, ct);
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
