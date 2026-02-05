using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Healthcare
{
    public class DicomConnectionStrategy : HealthcareConnectionStrategyBase
    {
        public override string StrategyId => "dicom";
        public override string DisplayName => "DICOM";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to DICOM medical imaging systems";
        public override string[] Tags => new[] { "dicom", "healthcare", "imaging", "pacs", "radiology" };

        public DicomConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = config.ConnectionString.Split(':');
            var client = new TcpClient();
            await client.ConnectAsync(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 104, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "DICOM" });
        }

        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(handle.GetConnection<TcpClient>().Connected);
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>().Close(); return Task.CompletedTask; }
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(handle.GetConnection<TcpClient>().Connected, "DICOM server", TimeSpan.Zero, DateTimeOffset.UtcNow));
        public override Task<(bool IsValid, string[] Errors)> ValidateHl7Async(IConnectionHandle handle, string hl7Message, CancellationToken ct = default) => throw new NotSupportedException("DICOM does not use HL7");
        public override Task<string> QueryFhirAsync(IConnectionHandle handle, string resourceType, string? query = null, CancellationToken ct = default) => throw new NotSupportedException("DICOM does not use FHIR");
    }
}
