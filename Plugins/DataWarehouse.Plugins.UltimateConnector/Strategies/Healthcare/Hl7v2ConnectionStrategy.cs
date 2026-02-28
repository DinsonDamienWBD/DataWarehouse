using System;
using System.Collections.Generic;
using System.Linq;
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
            var parts = (config.ConnectionString ?? throw new ArgumentException("Connection string required")).Split(':');
            var client = new TcpClient();
            await client.ConnectAsync(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 2575, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "HL7 v2.x/MLLP" });
        }

        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(handle.GetConnection<TcpClient>().Connected);
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>().Close(); return Task.CompletedTask; }
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(handle.GetConnection<TcpClient>().Connected, "HL7 server", TimeSpan.Zero, DateTimeOffset.UtcNow));
        public override Task<(bool IsValid, string[] Errors)> ValidateHl7Async(IConnectionHandle handle, string hl7Message, CancellationToken ct = default)
        {
            var errors = new List<string>();

            // Basic HL7 v2 structure validation
            if (string.IsNullOrWhiteSpace(hl7Message))
            {
                errors.Add("HL7 message cannot be empty");
                return Task.FromResult((false, errors.ToArray()));
            }

            var segments = hl7Message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length == 0 || !segments[0].StartsWith("MSH"))
            {
                errors.Add("HL7 message must start with MSH segment");
            }
            else
            {
                var mshFields = segments[0].Split('|');
                if (mshFields.Length < 12)
                {
                    errors.Add("MSH segment must have at least 12 fields");
                }
                if (mshFields.Length > 1 && mshFields[1].Length != 4)
                {
                    errors.Add("MSH-2 (encoding characters) must be exactly 4 characters");
                }
            }

            // Check for required segments based on message type
            if (!segments.Any(s => s.StartsWith("PID")))
            {
                errors.Add("Warning: PID segment is typically required");
            }

            return Task.FromResult((errors.Count == 0, errors.ToArray()));
        }

        public override Task<string> QueryFhirAsync(IConnectionHandle handle, string resourceType, string? query = null, CancellationToken ct = default)
        {
            // HL7 v2 does not support FHIR queries - provide guidance
            return Task.FromResult("{\"error\":\"HL7 v2 uses MLLP messaging, not FHIR REST. Use FhirR4ConnectionStrategy for FHIR queries.\"}");
        }

        /// <summary>
        /// Parses an HL7 v2.x message into structured segments and extracts key metadata.
        /// </summary>
        /// <param name="hl7Message">Raw HL7 v2.x message string.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Parsed HL7 message with segments, message type, and control ID.</returns>
        /// <exception cref="ArgumentException">Thrown if the message is invalid or missing the MSH segment.</exception>
        public async Task<Hl7ParsedMessage> ParseHl7MessageAsync(string hl7Message, CancellationToken ct = default)
        {
            await Task.CompletedTask; // Make async for consistency
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(hl7Message))
                throw new ArgumentException("HL7 message cannot be empty", nameof(hl7Message));

            // Split message into segments (typically delimited by \r or \n)
            var segmentStrings = hl7Message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            if (segmentStrings.Length == 0 || !segmentStrings[0].StartsWith("MSH"))
                throw new ArgumentException("HL7 message must start with MSH segment");

            var segments = new List<Hl7Segment>();
            string messageType = "UNKNOWN";
            string messageControlId = "UNKNOWN";

            foreach (var segmentString in segmentStrings)
            {
                ct.ThrowIfCancellationRequested();

                // Split segment into fields using '|' delimiter
                var fields = segmentString.Split('|');
                if (fields.Length == 0)
                    continue;

                string segmentId = fields[0];
                segments.Add(new Hl7Segment(segmentId, fields));

                // Extract metadata from MSH segment
                if (segmentId == "MSH" && fields.Length >= 12)
                {
                    // MSH-9: Message Type (e.g., ADT^A01)
                    if (fields.Length > 8)
                        messageType = fields[8];

                    // MSH-10: Message Control ID
                    if (fields.Length > 9)
                        messageControlId = fields[9];
                }
            }

            return new Hl7ParsedMessage(
                messageType,
                messageControlId,
                segments.ToArray(),
                hl7Message);
        }
    }
}
