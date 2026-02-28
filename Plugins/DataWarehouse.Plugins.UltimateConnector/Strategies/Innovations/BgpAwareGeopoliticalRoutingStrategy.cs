using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Innovations
{
    /// <summary>
    /// Routes data connections based on digital sovereignty laws and BGP path analysis.
    /// Inspects the AS path to the target endpoint via BGP looking glass services and
    /// validates that the route complies with configured geopolitical data residency
    /// requirements before establishing the connection.
    /// </summary>
    /// <remarks>
    /// Organizations subject to data sovereignty regulations (GDPR, CCPA, China's PIPL,
    /// Russia's data localization law) can use this strategy to enforce that no data
    /// transits through non-compliant autonomous systems or jurisdictions. The strategy
    /// queries public BGP looking glass APIs to resolve the AS path, maps each AS number
    /// to its registered country, and blocks connections whose routes violate the
    /// configured sovereignty policy.
    /// </remarks>
    public class BgpAwareGeopoliticalRoutingStrategy : ConnectionStrategyBase
    {
        /// <summary>
        /// Sovereignty policy rules mapping region codes to blocked country transit sets.
        /// </summary>
        private static readonly Dictionary<string, HashSet<string>> SovereigntyPolicies = new(StringComparer.OrdinalIgnoreCase)
        {
            ["eu-gdpr"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CN", "RU", "IR", "KP" },
            ["china-pipl"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "US", "GB", "AU", "JP", "IN" },
            ["russia-localization"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "US", "GB", "UA", "EE", "LV", "LT" },
            ["us-itar"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CN", "RU", "IR", "KP", "SY", "CU" },
            ["unrestricted"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };

        /// <inheritdoc/>
        public override string StrategyId => "innovation-bgp-routing";

        /// <inheritdoc/>
        public override string DisplayName => "BGP-Aware Geopolitical Routing";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: false,
            SupportsSsl: true,
            SupportsAuthentication: true,
            SupportsHealthCheck: true,
            SupportsConnectionTesting: true,
            SupportsReconnection: true,
            SupportedAuthMethods: ["bearer", "apikey", "none"],
            MaxConcurrentConnections: 50
        );

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Routes data connections based on digital sovereignty laws by inspecting BGP AS paths " +
            "and blocking routes that transit through non-compliant jurisdictions";

        /// <inheritdoc/>
        public override string[] Tags => ["bgp", "geopolitical", "sovereignty", "gdpr", "routing", "compliance", "data-residency"];

        /// <summary>
        /// Initializes a new instance of <see cref="BgpAwareGeopoliticalRoutingStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public BgpAwareGeopoliticalRoutingStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var targetEndpoint = config.ConnectionString
                ?? throw new ArgumentException("ConnectionString must specify the target endpoint.");

            var policy = GetConfiguration<string>(config, "sovereignty_policy", "unrestricted");
            var lookingGlassUrl = GetConfiguration<string>(config, "bgp_looking_glass_url", "https://stat.ripe.net/data/bgp-state/data.json");
            // Validate URL to prevent SSRF attacks
            if (Uri.TryCreate(lookingGlassUrl, UriKind.Absolute, out var bgpUri))
            { if (bgpUri.Host == "169.254.169.254" || bgpUri.Host == "metadata.google.internal" || bgpUri.Host == "localhost" || bgpUri.Host == "127.0.0.1") throw new ArgumentException("BGP looking glass URL points to a blocked internal endpoint (SSRF protection)"); }
            else throw new ArgumentException($"Invalid BGP looking glass URL: {lookingGlassUrl}");
            var enforcePolicy = GetConfiguration<bool>(config, "enforce_policy", true);
            var targetIp = GetConfiguration<string>(config, "target_ip", ExtractHostFromEndpoint(targetEndpoint));

            if (!SovereigntyPolicies.TryGetValue(policy, out var blockedCountries))
                throw new ArgumentException(
                    $"Unknown sovereignty policy '{policy}'. " +
                    $"Supported: {string.Join(", ", SovereigntyPolicies.Keys)}");

            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = config.PoolSize
            };

            var httpClient = new HttpClient(handler) { Timeout = config.Timeout };

            var bgpAnalysis = await AnalyzeBgpPathAsync(httpClient, lookingGlassUrl, targetIp, ct);
            var violations = ValidateRouteCompliance(bgpAnalysis.TransitCountries, blockedCountries);

            if (violations.Count > 0 && enforcePolicy)
            {
                httpClient.Dispose();
                throw new InvalidOperationException(
                    $"BGP route to {targetIp} violates sovereignty policy '{policy}'. " +
                    $"Route transits through blocked jurisdictions: {string.Join(", ", violations)}. " +
                    $"AS path: {string.Join(" -> ", bgpAnalysis.AsPath)}");
            }

            httpClient.BaseAddress = new Uri(targetEndpoint);

            using var response = await httpClient.GetAsync("/", ct);
            response.EnsureSuccessStatusCode();

            var info = new Dictionary<string, object>
            {
                ["target_endpoint"] = targetEndpoint,
                ["target_ip"] = targetIp,
                ["sovereignty_policy"] = policy,
                ["as_path"] = bgpAnalysis.AsPath,
                ["transit_countries"] = bgpAnalysis.TransitCountries,
                ["violations"] = violations,
                ["route_compliant"] = violations.Count == 0,
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(httpClient, info, $"bgp-{policy}-{targetIp}");
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            using var response = await client.GetAsync("/", ct);
            return response.IsSuccessStatusCode;
        }

        /// <inheritdoc/>
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            client.Dispose();
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();

            var isCompliant = handle.ConnectionInfo.TryGetValue("route_compliant", out var compliant)
                && compliant is true;

            return new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy
                    ? $"BGP-routed connection healthy, route compliant: {isCompliant}"
                    : "BGP-routed endpoint unreachable",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["route_compliant"] = isCompliant,
                    ["sovereignty_policy"] = handle.ConnectionInfo.GetValueOrDefault("sovereignty_policy", "unknown")!,
                    ["transit_countries"] = handle.ConnectionInfo.GetValueOrDefault("transit_countries", Array.Empty<string>())!
                });
        }

        /// <summary>
        /// Queries a BGP looking glass service to discover the AS path to the target IP.
        /// </summary>
        private static async Task<BgpPathAnalysis> AnalyzeBgpPathAsync(
            HttpClient client, string lookingGlassUrl, string targetIp, CancellationToken ct)
        {
            var requestUrl = $"{lookingGlassUrl}?resource={Uri.EscapeDataString(targetIp)}";

            try
            {
                using var response = await client.GetAsync(requestUrl, ct);
                if (!response.IsSuccessStatusCode)
                    return new BgpPathAnalysis([], []);

                var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                var asPath = new List<string>();
                var transitCountries = new List<string>();

                if (json.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("bgp_state", out var bgpState) &&
                    bgpState.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in bgpState.EnumerateArray())
                    {
                        if (entry.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var asn in path.EnumerateArray())
                            {
                                var asnStr = asn.GetString() ?? asn.ToString();
                                if (!asPath.Contains(asnStr))
                                    asPath.Add(asnStr);
                            }
                        }
                    }
                }

                foreach (var asn in asPath)
                {
                    var country = await ResolveAsnCountryAsync(client, asn, ct);
                    if (!string.IsNullOrEmpty(country) && !transitCountries.Contains(country))
                        transitCountries.Add(country);
                }

                return new BgpPathAnalysis(asPath, transitCountries);
            }
            catch (Exception)
            {
                return new BgpPathAnalysis([], []);
            }
        }

        /// <summary>
        /// Resolves the country registration for an autonomous system number via RIPE stat.
        /// </summary>
        private static async Task<string> ResolveAsnCountryAsync(
            HttpClient client, string asn, CancellationToken ct)
        {
            try
            {
                var url = $"https://stat.ripe.net/data/as-overview/data.json?resource=AS{asn.TrimStart('A', 'S', 'a', 's')}";
                using var response = await client.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) return string.Empty;

                var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                // Finding 1940: Read the explicit "country" field rather than the first 2 chars
                // of the "holder" name string, which is an organisation name not a country code.
                if (json.TryGetProperty("data", out var data))
                {
                    if (data.TryGetProperty("country", out var countryProp))
                    {
                        var countryStr = countryProp.GetString() ?? string.Empty;
                        if (countryStr.Length == 2)
                            return countryStr.ToUpperInvariant();
                    }
                    // Fallback: try "rir" → derive region (best-effort only)
                    if (data.TryGetProperty("rir", out var rirProp))
                    {
                        var rir = rirProp.GetString()?.ToUpperInvariant() ?? string.Empty;
                        // Map well-known RIR regions as a coarse fallback
                        return rir switch
                        {
                            "ARIN" => "US",
                            "RIPE NCC" => "EU",
                            "APNIC" => "AP",
                            "LACNIC" => "LA",
                            "AFRINIC" => "AF",
                            _ => string.Empty
                        };
                    }
                }
            }
            catch { /* Country resolution is best-effort */ }

            return string.Empty;
        }

        /// <summary>
        /// Validates the transit countries against the blocked set from the sovereignty policy.
        /// </summary>
        private static List<string> ValidateRouteCompliance(
            IReadOnlyList<string> transitCountries, HashSet<string> blockedCountries)
        {
            return transitCountries
                .Where(country => blockedCountries.Contains(country))
                .ToList();
        }

        /// <summary>
        /// Extracts the hostname or IP from an endpoint URL.
        /// </summary>
        private static string ExtractHostFromEndpoint(string endpoint)
        {
            if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                return uri.Host;
            var parts = endpoint.Split(':');
            return parts[0];
        }

        /// <summary>
        /// Result of BGP path analysis including AS numbers and transit countries.
        /// </summary>
        private sealed record BgpPathAnalysis(
            IReadOnlyList<string> AsPath,
            IReadOnlyList<string> TransitCountries);
    }
}
