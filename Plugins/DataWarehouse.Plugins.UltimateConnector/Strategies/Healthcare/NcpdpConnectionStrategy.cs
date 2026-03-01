using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Healthcare
{
    public sealed class NcpdpConnectionStrategy : HealthcareConnectionStrategyBase
    {
        public override string StrategyId => "ncpdp";
        public override string DisplayName => "NCPDP";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to NCPDP pharmacy transaction systems";
        public override string[] Tags => new[] { "ncpdp", "healthcare", "pharmacy", "prescriptions", "transactions" };

        public NcpdpConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            // P2-2132: Use ParseHostPortSafe to correctly handle IPv6 addresses like [::1]:5555
            var (host, port) = ParseHostPortSafe(config.ConnectionString ?? throw new ArgumentException("Connection string required"), 5555);
            var client = new TcpClient();
            await client.ConnectAsync(host, port, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "NCPDP/TCP" });
        }

        // Finding 1919: Use live socket probe instead of stale Connected flag.
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<TcpClient>();
            if (!client.Connected) return false;
            try { await client.GetStream().WriteAsync(Array.Empty<byte>(), ct); return true; }
            catch { return false; }
        }

        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>().Close(); return Task.CompletedTask; }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "NCPDP server connected" : "NCPDP server disconnected", sw.Elapsed, DateTimeOffset.UtcNow);
        }
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
