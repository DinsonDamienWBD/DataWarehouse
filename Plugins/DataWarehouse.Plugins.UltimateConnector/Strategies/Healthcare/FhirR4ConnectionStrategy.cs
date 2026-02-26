using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
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
            using var response = await client.GetAsync("/metadata", ct);
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
            using var response = await client.GetAsync(url, ct);
 response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }

        /// <summary>
        /// Deserializes a FHIR resource from JSON and extracts key metadata.
        /// </summary>
        /// <param name="json">JSON string containing a FHIR resource.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Wrapped FHIR resource with extracted metadata and full JSON content.</returns>
        /// <exception cref="ArgumentException">Thrown if the JSON is invalid or missing required fields.</exception>
        public async Task<FhirResourceWrapper> DeserializeResourceAsync(string json, CancellationToken ct = default)
        {
            await Task.CompletedTask; // Make async for consistency
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON cannot be empty", nameof(json));

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new ArgumentException("Invalid JSON format", nameof(json), ex);
            }

            var root = document.RootElement;

            // Extract resourceType (required)
            if (!root.TryGetProperty("resourceType", out var resourceTypeElement))
                throw new ArgumentException("FHIR resource must have a 'resourceType' property");

            string resourceType = resourceTypeElement.GetString() ?? "UNKNOWN";

            // Extract id (required)
            string id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? "UNKNOWN" : "UNKNOWN";

            // Extract meta (optional)
            string? versionId = null;
            DateTimeOffset? lastUpdated = null;

            if (root.TryGetProperty("meta", out var metaElement))
            {
                if (metaElement.TryGetProperty("versionId", out var versionIdElement))
                    versionId = versionIdElement.GetString();

                if (metaElement.TryGetProperty("lastUpdated", out var lastUpdatedElement))
                {
                    string? lastUpdatedStr = lastUpdatedElement.GetString();
                    if (!string.IsNullOrEmpty(lastUpdatedStr) && DateTimeOffset.TryParse(lastUpdatedStr, out var parsed))
                        lastUpdated = parsed;
                }
            }

            var meta = new FhirMeta(versionId, lastUpdated);

            return new FhirResourceWrapper(resourceType, id, meta, root.Clone());
        }
    }
}
