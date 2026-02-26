using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Innovations
{
    /// <summary>
    /// Jurisdiction-aware connection routing strategy that ensures data sovereignty compliance
    /// with GDPR (EU), PIPL (China), FZ-152 (Russia), DPDP (India), LGPD (Brazil), POPIA (South Africa),
    /// and other regional data protection regulations.
    /// </summary>
    /// <remarks>
    /// The strategy resolves the target endpoint based on data classification, user jurisdiction,
    /// and regulatory requirements:
    /// <list type="bullet">
    ///   <item>Classifies data sensitivity level (public, internal, confidential, restricted)</item>
    ///   <item>Determines applicable regulations from source/destination jurisdictions</item>
    ///   <item>Routes connections to compliant regional endpoints</item>
    ///   <item>Enforces data residency constraints at the connection level</item>
    ///   <item>Logs all cross-border transfer decisions for audit compliance</item>
    /// </list>
    /// </remarks>
    public class DataSovereigntyRouterStrategy : ConnectionStrategyBase
    {
        private static readonly Dictionary<string, string[]> JurisdictionRegulations = new(StringComparer.OrdinalIgnoreCase)
        {
            ["EU"] = ["GDPR"],
            ["DE"] = ["GDPR", "BDSG"],
            ["FR"] = ["GDPR", "CNIL"],
            ["CN"] = ["PIPL", "CSL", "DSL"],
            ["RU"] = ["FZ-152"],
            ["IN"] = ["DPDP"],
            ["BR"] = ["LGPD"],
            ["ZA"] = ["POPIA"],
            ["US-CA"] = ["CCPA", "CPRA"],
            ["US-VA"] = ["VCDPA"],
            ["US-CO"] = ["CPA"],
            ["JP"] = ["APPI"],
            ["KR"] = ["PIPA"],
            ["AU"] = ["APPs"],
            ["CA"] = ["PIPEDA"]
        };

        /// <inheritdoc/>
        public override string StrategyId => "innovation-data-sovereignty";

        /// <inheritdoc/>
        public override string DisplayName => "Data Sovereignty Router";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: false,
            SupportsSsl: true,
            SupportsAuthentication: true,
            RequiresAuthentication: true,
            SupportsHealthCheck: true,
            SupportsConnectionTesting: true,
            MaxConcurrentConnections: 100,
            SupportedAuthMethods: ["bearer", "oauth2"]
        );

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Jurisdiction-aware connection routing ensuring data sovereignty compliance with " +
            "GDPR, PIPL, FZ-152, DPDP, LGPD, and other regional data protection regulations";

        /// <inheritdoc/>
        public override string[] Tags => ["sovereignty", "gdpr", "pipl", "compliance", "jurisdiction", "data-residency", "routing"];

        /// <summary>
        /// Initializes a new instance of <see cref="DataSovereigntyRouterStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public DataSovereigntyRouterStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var routerEndpoint = config.ConnectionString
                ?? throw new ArgumentException("Sovereignty router endpoint URL is required in ConnectionString.");

            var sourceJurisdiction = GetConfiguration<string>(config, "source_jurisdiction", "")
                ?? throw new ArgumentException("source_jurisdiction property is required.");
            var destinationJurisdiction = GetConfiguration<string>(config, "destination_jurisdiction", "");
            var dataClassification = GetConfiguration<string>(config, "data_classification", "internal");
            var dataCategories = GetConfiguration<string>(config, "data_categories", "general");
            var consentBasis = GetConfiguration<string>(config, "consent_basis", "legitimate_interest");
            var transferMechanism = GetConfiguration<string>(config, "transfer_mechanism", "auto");

            if (string.IsNullOrWhiteSpace(sourceJurisdiction))
                throw new ArgumentException("source_jurisdiction property is required for sovereignty routing.");

            var applicableRegulations = ResolveApplicableRegulations(sourceJurisdiction, destinationJurisdiction);

            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = config.PoolSize
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(routerEndpoint),
                Timeout = config.Timeout,
                DefaultRequestVersion = new Version(2, 0)
            };

            if (!string.IsNullOrEmpty(config.AuthCredential))
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.AuthCredential);

            var routingRequest = new
            {
                source_jurisdiction = sourceJurisdiction,
                destination_jurisdiction = destinationJurisdiction,
                data_classification = dataClassification,
                data_categories = dataCategories.Split(',', StringSplitOptions.RemoveEmptyEntries),
                applicable_regulations = applicableRegulations,
                consent_basis = consentBasis,
                transfer_mechanism = transferMechanism,
                require_adequacy_decision = dataClassification is "confidential" or "restricted",
                request_timestamp = DateTimeOffset.UtcNow
            };

            var content = new StringContent(
                JsonSerializer.Serialize(routingRequest),
                Encoding.UTF8,
                "application/json");

            var routingResponse = await client.PostAsync("/api/v1/sovereignty/resolve", content, ct);
            routingResponse.EnsureSuccessStatusCode();

            var routingResult = await routingResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
            var resolvedEndpoint = routingResult.GetProperty("resolved_endpoint").GetString()
                ?? throw new InvalidOperationException("Sovereignty router did not return a resolved_endpoint.");
            var routeId = routingResult.GetProperty("route_id").GetString() ?? Guid.NewGuid().ToString("N");
            var complianceStatus = routingResult.TryGetProperty("compliance_status", out var cs)
                ? cs.GetString() ?? "unknown" : "unknown";
            var selectedTransferMechanism = routingResult.TryGetProperty("transfer_mechanism", out var tm)
                ? tm.GetString() ?? transferMechanism : transferMechanism;

            if (complianceStatus == "blocked")
                throw new InvalidOperationException(
                    $"Data transfer blocked by sovereignty rules: {sourceJurisdiction} -> {destinationJurisdiction}. " +
                    $"Regulations: {string.Join(", ", applicableRegulations)}");

            client.DefaultRequestHeaders.Remove("X-Sovereignty-Route");
            client.DefaultRequestHeaders.Add("X-Sovereignty-Route", routeId);
            client.DefaultRequestHeaders.Remove("X-Data-Classification");
            client.DefaultRequestHeaders.Add("X-Data-Classification", dataClassification);
            client.DefaultRequestHeaders.Remove("X-Source-Jurisdiction");
            client.DefaultRequestHeaders.Add("X-Source-Jurisdiction", sourceJurisdiction);

            var verifyResponse = await client.GetAsync($"/api/v1/sovereignty/routes/{routeId}/verify", ct);
            verifyResponse.EnsureSuccessStatusCode();

            var info = new Dictionary<string, object>
            {
                ["router_endpoint"] = routerEndpoint,
                ["resolved_endpoint"] = resolvedEndpoint,
                ["route_id"] = routeId,
                ["source_jurisdiction"] = sourceJurisdiction,
                ["destination_jurisdiction"] = destinationJurisdiction,
                ["data_classification"] = dataClassification,
                ["applicable_regulations"] = string.Join(",", applicableRegulations),
                ["compliance_status"] = complianceStatus,
                ["transfer_mechanism"] = selectedTransferMechanism,
                ["consent_basis"] = consentBasis,
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info, $"sov-{routeId}");
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var routeId = handle.ConnectionInfo["route_id"]?.ToString();

            using var response = await client.GetAsync($"/api/v1/sovereignty/routes/{routeId}/status", ct);
            if (!response.IsSuccessStatusCode) return false;

            var status = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var compliance = status.TryGetProperty("compliance_status", out var cs) ? cs.GetString() : "unknown";
            return compliance is "compliant" or "approved";
        }

        /// <inheritdoc/>
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var routeId = handle.ConnectionInfo["route_id"]?.ToString();

            try
            {
                var auditPayload = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        route_id = routeId,
                        action = "disconnect",
                        timestamp = DateTimeOffset.UtcNow
                    }),
                    Encoding.UTF8,
                    "application/json");

                await client.PostAsync("/api/v1/sovereignty/audit", auditPayload, ct);
                await client.DeleteAsync($"/api/v1/sovereignty/routes/{routeId}", ct);
            }
            finally
            {
                client.Dispose();
            }
        }

        /// <inheritdoc/>
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var client = handle.GetConnection<HttpClient>();
            var routeId = handle.ConnectionInfo["route_id"]?.ToString();

            using var response = await client.GetAsync($"/api/v1/sovereignty/routes/{routeId}/health", ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ConnectionHealth(
                    IsHealthy: false,
                    StatusMessage: "Sovereignty route health check failed",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow);
            }

            var healthData = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var isCompliant = healthData.TryGetProperty("compliant", out var comp) && comp.GetBoolean();

            return new ConnectionHealth(
                IsHealthy: isCompliant,
                StatusMessage: isCompliant
                    ? $"Route compliant: {handle.ConnectionInfo.GetValueOrDefault("source_jurisdiction", "")} -> {handle.ConnectionInfo.GetValueOrDefault("destination_jurisdiction", "")}"
                    : "Route compliance violation detected",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["compliant"] = isCompliant,
                    ["regulations"] = handle.ConnectionInfo.GetValueOrDefault("applicable_regulations", ""),
                    ["transfer_mechanism"] = handle.ConnectionInfo.GetValueOrDefault("transfer_mechanism", ""),
                    ["route_id"] = routeId ?? ""
                });
        }

        /// <summary>
        /// Resolves which data protection regulations apply given source and destination jurisdictions.
        /// </summary>
        private static List<string> ResolveApplicableRegulations(string source, string destination)
        {
            var regulations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (JurisdictionRegulations.TryGetValue(source, out var sourceRegs))
                foreach (var reg in sourceRegs) regulations.Add(reg);

            if (!string.IsNullOrEmpty(destination) && JurisdictionRegulations.TryGetValue(destination, out var destRegs))
                foreach (var reg in destRegs) regulations.Add(reg);

            if (source.StartsWith("EU", StringComparison.OrdinalIgnoreCase)
                || (JurisdictionRegulations.TryGetValue(source, out var sr) && sr.Contains("GDPR")))
            {
                if (!string.IsNullOrEmpty(destination) && !destination.StartsWith("EU", StringComparison.OrdinalIgnoreCase))
                    regulations.Add("GDPR-Transfer");
            }

            return regulations.ToList();
        }
    }
}
