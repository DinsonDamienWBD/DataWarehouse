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
    /// Automated API healing strategy that detects API contract changes (new fields, removed
    /// endpoints, changed response shapes, version deprecations) and auto-adapts connection
    /// behavior, request mappings, and response parsing in real-time.
    /// </summary>
    /// <remarks>
    /// The healing engine operates in several modes:
    /// <list type="bullet">
    ///   <item>Version detection: discovers available API versions and negotiates the best match</item>
    ///   <item>Schema drift detection: compares current responses against baseline schemas</item>
    ///   <item>Endpoint migration: detects 301/308 redirects and updates base paths</item>
    ///   <item>Field mapping: auto-maps renamed or restructured response fields</item>
    ///   <item>Deprecation tracking: monitors Sunset/Deprecation headers for proactive migration</item>
    ///   <item>Breaking change isolation: quarantines affected endpoints while adapting</item>
    /// </list>
    /// </remarks>
    public class AutomatedApiHealingStrategy : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "innovation-api-healing";

        /// <inheritdoc/>
        public override string DisplayName => "Automated API Healing";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: false,
            SupportsSchemaDiscovery: true,
            SupportsSsl: true,
            SupportsCompression: true,
            SupportsHealthCheck: true,
            SupportsConnectionTesting: true,
            SupportsReconnection: true,
            MaxConcurrentConnections: 100
        );

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Automated API healing that detects contract changes, version deprecations, and schema drift " +
            "then auto-adapts connection behavior, request mappings, and response parsing in real-time";

        /// <inheritdoc/>
        public override string[] Tags => ["api-healing", "auto-adapt", "versioning", "schema-drift", "deprecation", "migration"];

        /// <summary>
        /// Initializes a new instance of <see cref="AutomatedApiHealingStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public AutomatedApiHealingStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString
                ?? throw new ArgumentException("API endpoint URL is required in ConnectionString.");

            var preferredVersion = GetConfiguration<string>(config, "preferred_api_version", "latest");
            var enableSchemaDrift = GetConfiguration<bool>(config, "enable_schema_drift_detection", true);
            var enableAutoMigration = GetConfiguration<bool>(config, "enable_auto_migration", true);
            var enableDeprecationTracking = GetConfiguration<bool>(config, "enable_deprecation_tracking", true);
            var baselineCaptureOnConnect = GetConfiguration<bool>(config, "baseline_on_connect", true);
            var healingMode = GetConfiguration<string>(config, "healing_mode", "adaptive");

            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = config.PoolSize,
                AllowAutoRedirect = false
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(endpoint),
                Timeout = config.Timeout,
                DefaultRequestVersion = new Version(2, 0)
            };

            if (!string.IsNullOrEmpty(config.AuthCredential))
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.AuthCredential);

            var versionInfo = await DetectApiVersionAsync(client, preferredVersion, ct);

            DeprecationInfo? deprecationInfo = null;
            if (enableDeprecationTracking)
            {
                deprecationInfo = await CheckDeprecationAsync(client, ct);
            }

            string? baselineSchemaHash = null;
            if (baselineCaptureOnConnect && enableSchemaDrift)
            {
                baselineSchemaHash = await CaptureBaselineSchemaAsync(client, ct);
            }

            var registrationPayload = new
            {
                api_version = versionInfo.ActiveVersion,
                available_versions = versionInfo.AvailableVersions,
                healing = new
                {
                    mode = healingMode,
                    schema_drift_detection = enableSchemaDrift,
                    auto_migration = enableAutoMigration,
                    deprecation_tracking = enableDeprecationTracking,
                    baseline_schema_hash = baselineSchemaHash,
                    redirect_follow_limit = GetConfiguration<int>(config, "redirect_follow_limit", 5),
                    quarantine_on_breaking_change = GetConfiguration<bool>(config, "quarantine_breaking", true)
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(registrationPayload),
                Encoding.UTF8,
                "application/json");

            using var response = await client.PostAsync("/api/v1/healing/sessions", content, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var sessionId = result.GetProperty("session_id").GetString()
                ?? throw new InvalidOperationException("API healing endpoint did not return a session_id.");

            client.DefaultRequestHeaders.Remove("X-Healing-Session");
            client.DefaultRequestHeaders.Add("X-Healing-Session", sessionId);
            if (!string.IsNullOrEmpty(versionInfo.ActiveVersion))
                client.DefaultRequestHeaders.Remove("X-API-Version");
                client.DefaultRequestHeaders.Add("X-API-Version", versionInfo.ActiveVersion);

            var info = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["session_id"] = sessionId,
                ["api_version"] = versionInfo.ActiveVersion,
                ["available_versions"] = string.Join(",", versionInfo.AvailableVersions),
                ["healing_mode"] = healingMode,
                ["schema_drift_enabled"] = enableSchemaDrift,
                ["auto_migration_enabled"] = enableAutoMigration,
                ["deprecation_tracking"] = enableDeprecationTracking,
                ["baseline_schema_hash"] = baselineSchemaHash ?? "none",
                ["deprecation_status"] = deprecationInfo?.Status ?? "none",
                ["sunset_date"] = deprecationInfo?.SunsetDate?.ToString("O") ?? "none",
                ["healed_count"] = 0,
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info, $"heal-{sessionId}");
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var sessionId = handle.ConnectionInfo["session_id"]?.ToString();

            using var response = await client.GetAsync($"/api/v1/healing/sessions/{sessionId}/status", ct);
            if (!response.IsSuccessStatusCode) return false;

            var status = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return status.TryGetProperty("active", out var active) && active.GetBoolean();
        }

        /// <inheritdoc/>
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var sessionId = handle.ConnectionInfo["session_id"]?.ToString();

            try
            {
                var reportResponse = await client.GetAsync(
                    $"/api/v1/healing/sessions/{sessionId}/report", ct);

                await client.DeleteAsync($"/api/v1/healing/sessions/{sessionId}", ct);
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
            var sessionId = handle.ConnectionInfo["session_id"]?.ToString();

            using var response = await client.GetAsync($"/api/v1/healing/sessions/{sessionId}/health", ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ConnectionHealth(
                    IsHealthy: false,
                    StatusMessage: "API healing session health check failed",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow);
            }

            var healthData = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var isActive = healthData.TryGetProperty("active", out var a) && a.GetBoolean();
            var driftDetected = healthData.TryGetProperty("drift_detected", out var dd) && dd.GetBoolean();
            var healedEndpoints = healthData.TryGetProperty("healed_endpoints", out var he) ? he.GetInt32() : 0;
            var quarantinedEndpoints = healthData.TryGetProperty("quarantined_endpoints", out var qe) ? qe.GetInt32() : 0;
            var deprecationWarning = healthData.TryGetProperty("deprecation_warning", out var dw) && dw.GetBoolean();

            var statusMessage = isActive
                ? driftDetected
                    ? $"API healing active, schema drift detected, {healedEndpoints} healed, {quarantinedEndpoints} quarantined"
                    : $"API healing active, no drift, {healedEndpoints} healed"
                : "API healing session inactive";

            if (deprecationWarning)
                statusMessage += " [DEPRECATION WARNING]";

            return new ConnectionHealth(
                IsHealthy: isActive && quarantinedEndpoints == 0,
                StatusMessage: statusMessage,
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["active"] = isActive,
                    ["drift_detected"] = driftDetected,
                    ["healed_endpoints"] = healedEndpoints,
                    ["quarantined_endpoints"] = quarantinedEndpoints,
                    ["deprecation_warning"] = deprecationWarning,
                    ["api_version"] = handle.ConnectionInfo.GetValueOrDefault("api_version", "unknown"),
                    ["healing_mode"] = handle.ConnectionInfo.GetValueOrDefault("healing_mode", "unknown")
                });
        }

        /// <summary>
        /// Detects available API versions and selects the best match for the preferred version.
        /// </summary>
        private static async Task<ApiVersionInfo> DetectApiVersionAsync(
            HttpClient client, string preferredVersion, CancellationToken ct)
        {
            var availableVersions = new List<string>();
            string activeVersion = preferredVersion;

            try
            {
                using var response = await client.GetAsync("/api/versions", ct);
                if (response.IsSuccessStatusCode)
                {
                    var versionsData = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                    if (versionsData.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var v in versions.EnumerateArray())
                        {
                            var ver = v.GetString();
                            if (ver != null) availableVersions.Add(ver);
                        }
                    }

                    if (preferredVersion == "latest" && availableVersions.Count > 0)
                        activeVersion = availableVersions[^1];
                    else if (availableVersions.Contains(preferredVersion))
                        activeVersion = preferredVersion;
                    else if (availableVersions.Count > 0)
                        activeVersion = availableVersions[^1];
                }
            }
            catch
            {

                // Version detection is best-effort
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }

            if (availableVersions.Count == 0)
                availableVersions.Add(activeVersion);

            return new ApiVersionInfo(activeVersion, availableVersions.ToArray());
        }

        /// <summary>
        /// Checks for API deprecation headers (Sunset, Deprecation per RFC 8594).
        /// </summary>
        private static async Task<DeprecationInfo> CheckDeprecationAsync(HttpClient client, CancellationToken ct)
        {
            try
            {
                using var response = await client.SendAsync(
                    new HttpRequestMessage(HttpMethod.Head, "/"), ct);

                DateTimeOffset? sunsetDate = null;
                string status = "none";

                if (response.Headers.TryGetValues("Sunset", out var sunsetValues))
                {
                    var sunsetStr = sunsetValues.FirstOrDefault();
                    if (DateTimeOffset.TryParse(sunsetStr, out var parsed))
                    {
                        sunsetDate = parsed;
                        status = parsed <= DateTimeOffset.UtcNow ? "expired" : "scheduled";
                    }
                }

                if (response.Headers.TryGetValues("Deprecation", out var deprecationValues))
                {
                    var depStr = deprecationValues.FirstOrDefault();
                    if (depStr?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
                        status = status == "none" ? "deprecated" : status;
                }

                return new DeprecationInfo(status, sunsetDate);
            }
            catch
            {
                return new DeprecationInfo("unknown", null);
            }
        }

        /// <summary>
        /// Captures a baseline schema fingerprint from the API's current response shape.
        /// </summary>
        private static async Task<string?> CaptureBaselineSchemaAsync(HttpClient client, CancellationToken ct)
        {
            try
            {
                using var response = await client.GetAsync("/api/v1/schema", ct);
                if (!response.IsSuccessStatusCode) return null;

                var body = await response.Content.ReadAsByteArrayAsync(ct);
                var hash = System.Security.Cryptography.SHA256.HashData(body);
                return Convert.ToHexString(hash)[..16];
            }
            catch
            {
                return null;
            }
        }

        private record ApiVersionInfo(string ActiveVersion, string[] AvailableVersions);
        private record DeprecationInfo(string Status, DateTimeOffset? SunsetDate);
    }
}
