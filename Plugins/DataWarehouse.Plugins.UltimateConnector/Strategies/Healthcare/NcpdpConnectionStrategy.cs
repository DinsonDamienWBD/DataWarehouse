using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Healthcare
{
    public class NcpdpConnectionStrategy : HealthcareConnectionStrategyBase
    {
        public override string StrategyId => "ncpdp";
        public override string DisplayName => "NCPDP";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to NCPDP pharmacy transaction systems";
        public override string[] Tags => new[] { "ncpdp", "healthcare", "pharmacy", "prescriptions", "transactions" };

        public NcpdpConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = (config.ConnectionString ?? throw new ArgumentException("Connection string required")).Split(':');
            var client = new TcpClient();
            await client.ConnectAsync(parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p5555) ? p5555 : 5555, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "NCPDP/TCP" });
        }

        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(handle.GetConnection<TcpClient>().Connected);
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>().Close(); return Task.CompletedTask; }
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(handle.GetConnection<TcpClient>().Connected, "NCPDP server", TimeSpan.Zero, DateTimeOffset.UtcNow));
        public override Task<(bool IsValid, string[] Errors)> ValidateHl7Async(IConnectionHandle handle, string hl7Message, CancellationToken ct = default)
        {
            // NCPDP uses its own format for pharmacy transactions
            return Task.FromResult((false, new[] { "NCPDP uses NCPDP Telecommunication Standard, not HL7 v2. Use Hl7v2ConnectionStrategy for HL7 validation." }));
        }

        public override Task<string> QueryFhirAsync(IConnectionHandle handle, string resourceType, string? query = null, CancellationToken ct = default)
        {
            // NCPDP uses its own protocol
            return Task.FromResult("{\"error\":\"NCPDP uses NCPDP SCRIPT for e-prescribing. For FHIR MedicationRequest resources, use FhirR4ConnectionStrategy.\"}");
        }
    }
}
