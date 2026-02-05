using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Healthcare
{
    public class Hl7v2ConnectionStrategy : HealthcareConnectionStrategyBase
    {
        public override string StrategyId => "hl7v2";
        public override string DisplayName => "HL7 v2.x";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to HL7 v2.x messaging systems via MLLP protocol";
        public override string[] Tags => new[] { "hl7", "healthcare", "mllp", "edi", "messaging" };

        public Hl7v2ConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = config.ConnectionString.Split(':');
            var client = new TcpClient();
            await client.ConnectAsync(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 2575, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "HL7 v2.x/MLLP" });
        }

        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(handle.GetConnection<TcpClient>().Connected);
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>().Close(); return Task.CompletedTask; }
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(handle.GetConnection<TcpClient>().Connected, "HL7 server", TimeSpan.Zero, DateTimeOffset.UtcNow));
        public override Task<(bool IsValid, string[] Errors)> ValidateHl7Async(IConnectionHandle handle, string hl7Message, CancellationToken ct = default) => throw new NotSupportedException("Requires HL7 parser");
        public override Task<string> QueryFhirAsync(IConnectionHandle handle, string resourceType, string? query = null, CancellationToken ct = default) => throw new NotSupportedException("Use FHIR strategy for FHIR queries");
    }
}
