using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using IHealthCheck = DataWarehouse.SDK.Infrastructure.IHealthCheck;
using HealthCheckResult = DataWarehouse.SDK.Infrastructure.HealthCheckResult;
using HealthStatus = DataWarehouse.SDK.Infrastructure.HealthStatus;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.SDK.Security.SupplyChain
{
    // ──────────────────────────────────────────────────────────────
    // Vulnerability types
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Severity levels for vulnerability findings.
    /// </summary>
    public enum VulnSeverity
    {
        /// <summary>Informational only.</summary>
        None,
        /// <summary>Low severity — limited impact.</summary>
        Low,
        /// <summary>Medium severity — moderate impact under certain conditions.</summary>
        Medium,
        /// <summary>High severity — significant impact, exploitation likely.</summary>
        High,
        /// <summary>Critical severity — immediate exploitation risk, maximum impact.</summary>
        Critical
    }

    /// <summary>
    /// A single vulnerability finding for a package dependency.
    /// </summary>
    public sealed class VulnerabilityFinding
    {
        /// <summary>Affected package name.</summary>
        public string PackageName { get; init; } = string.Empty;

        /// <summary>Installed package version.</summary>
        public string PackageVersion { get; init; } = string.Empty;

        /// <summary>CVE identifier (e.g., CVE-2023-12345).</summary>
        public string CveId { get; init; } = string.Empty;

        /// <summary>Vulnerability severity level.</summary>
        public VulnSeverity Severity { get; init; }

        /// <summary>Human-readable vulnerability description.</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>Version that fixes this vulnerability, if known.</summary>
        public string? FixedInVersion { get; init; }

        /// <summary>When this vulnerability was published.</summary>
        public DateTimeOffset? Published { get; init; }

        /// <summary>Reference URLs for more information.</summary>
        public IReadOnlyList<Uri> References { get; init; } = Array.Empty<Uri>();
    }

    /// <summary>
    /// Result of a dependency vulnerability scan.
    /// </summary>
    public sealed class DependencyScanResult
    {
        /// <summary>When the scan was performed.</summary>
        public DateTimeOffset ScannedAt { get; init; }

        /// <summary>Number of components that were scanned.</summary>
        public int ComponentsScanned { get; init; }

        /// <summary>Total vulnerabilities found across all components.</summary>
        public int VulnerabilitiesFound { get; init; }

        /// <summary>Individual vulnerability findings.</summary>
        public IReadOnlyList<VulnerabilityFinding> Vulnerabilities { get; init; } =
            Array.Empty<VulnerabilityFinding>();

        /// <summary>Count of Critical severity findings.</summary>
        public int CriticalCount => Vulnerabilities.Count(v => v.Severity == VulnSeverity.Critical);

        /// <summary>Count of High severity findings.</summary>
        public int HighCount => Vulnerabilities.Count(v => v.Severity == VulnSeverity.High);

        /// <summary>Count of Medium severity findings.</summary>
        public int MediumCount => Vulnerabilities.Count(v => v.Severity == VulnSeverity.Medium);

        /// <summary>Count of Low severity findings.</summary>
        public int LowCount => Vulnerabilities.Count(v => v.Severity == VulnSeverity.Low);

        /// <summary>Whether any Critical or High vulnerabilities were found.</summary>
        public bool HasHighOrCritical => CriticalCount > 0 || HighCount > 0;
    }

    // ──────────────────────────────────────────────────────────────
    // Scanner interfaces
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Interface for dependency vulnerability scanning.
    /// </summary>
    public interface IDependencyScanner
    {
        /// <summary>
        /// Scan an existing SBOM document for known vulnerabilities.
        /// </summary>
        Task<DependencyScanResult> ScanAsync(SbomDocument sbom, CancellationToken ct = default);

        /// <summary>
        /// Generate an SBOM from runtime assemblies and scan it for vulnerabilities.
        /// </summary>
        Task<DependencyScanResult> ScanAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Interface for vulnerability data sources (offline embedded DB or online APIs).
    /// </summary>
    public interface IVulnerabilityDatabase
    {
        /// <summary>
        /// Query for known vulnerabilities affecting a specific package version.
        /// </summary>
        Task<IReadOnlyList<VulnerabilityFinding>> QueryAsync(
            string packageName, string version, CancellationToken ct = default);
    }

    /// <summary>
    /// Interface for scheduling periodic vulnerability scans.
    /// </summary>
    public interface IScanScheduler : IDisposable
    {
        /// <summary>Start scheduled scanning at the configured interval.</summary>
        Task StartAsync(CancellationToken ct = default);

        /// <summary>Stop scheduled scanning.</summary>
        Task StopAsync(CancellationToken ct = default);

        /// <summary>The interval between scans.</summary>
        TimeSpan ScanInterval { get; set; }
    }

    // ──────────────────────────────────────────────────────────────
    // Embedded vulnerability database
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Offline vulnerability database containing known critical NuGet CVEs.
    /// Checks packages against an embedded database of the most common vulnerabilities.
    /// </summary>
    public sealed class EmbeddedVulnerabilityDatabase : IVulnerabilityDatabase
    {
        private readonly Lazy<Dictionary<string, List<KnownVulnerability>>> _database;

        /// <summary>
        /// Creates a new embedded vulnerability database instance.
        /// </summary>
        public EmbeddedVulnerabilityDatabase()
        {
            _database = new Lazy<Dictionary<string, List<KnownVulnerability>>>(BuildDatabase);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<VulnerabilityFinding>> QueryAsync(
            string packageName, string version, CancellationToken ct = default)
        {
            var findings = new List<VulnerabilityFinding>();
            var db = _database.Value;

            if (!db.TryGetValue(packageName.ToLowerInvariant(), out var vulns))
                return Task.FromResult<IReadOnlyList<VulnerabilityFinding>>(findings);

            if (!TryParseVersion(version, out var currentVersion))
                return Task.FromResult<IReadOnlyList<VulnerabilityFinding>>(findings);

            foreach (var vuln in vulns)
            {
                if (IsVersionAffected(currentVersion, vuln.AffectedVersionRange))
                {
                    findings.Add(new VulnerabilityFinding
                    {
                        PackageName = packageName,
                        PackageVersion = version,
                        CveId = vuln.CveId,
                        Severity = vuln.Severity,
                        Description = vuln.Description,
                        FixedInVersion = vuln.FixedInVersion,
                        Published = vuln.Published,
                        References = vuln.References
                    });
                }
            }

            return Task.FromResult<IReadOnlyList<VulnerabilityFinding>>(findings);
        }

        private static bool TryParseVersion(string versionStr, out Version version)
        {
            // Handle semver pre-release suffixes
            var dashIndex = versionStr.IndexOf('-');
            if (dashIndex >= 0)
                versionStr = versionStr[..dashIndex];

            return Version.TryParse(versionStr, out version!);
        }

        private static bool IsVersionAffected(Version currentVersion, string rangeSpec)
        {
            // Parse version range format: ">=minVersion,<maxVersion" or "<maxVersion"
            var parts = rangeSpec.Split(',');
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith(">="))
                {
                    if (TryParseVersion(trimmed[2..], out var min) && currentVersion < min)
                        return false;
                }
                else if (trimmed.StartsWith(">"))
                {
                    if (TryParseVersion(trimmed[1..], out var min) && currentVersion <= min)
                        return false;
                }
                else if (trimmed.StartsWith("<="))
                {
                    if (TryParseVersion(trimmed[2..], out var max) && currentVersion > max)
                        return false;
                }
                else if (trimmed.StartsWith("<"))
                {
                    if (TryParseVersion(trimmed[1..], out var max) && currentVersion >= max)
                        return false;
                }
                else if (trimmed.StartsWith("=="))
                {
                    if (TryParseVersion(trimmed[2..], out var exact) && currentVersion != exact)
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Builds the embedded vulnerability database with known critical NuGet CVEs.
        /// This is a curated subset of the most impactful vulnerabilities for .NET packages.
        /// </summary>
        private static Dictionary<string, List<KnownVulnerability>> BuildDatabase()
        {
            var db = new Dictionary<string, List<KnownVulnerability>>(StringComparer.OrdinalIgnoreCase);

            void Add(string package, string cve, VulnSeverity severity, string desc,
                string affected, string? fixedIn, DateTimeOffset? published, params string[] refs)
            {
                if (!db.TryGetValue(package.ToLowerInvariant(), out var list))
                {
                    list = new List<KnownVulnerability>();
                    db[package.ToLowerInvariant()] = list;
                }
                list.Add(new KnownVulnerability
                {
                    CveId = cve,
                    Severity = severity,
                    Description = desc,
                    AffectedVersionRange = affected,
                    FixedInVersion = fixedIn,
                    Published = published,
                    References = refs.Select(r => new Uri(r)).ToList()
                });
            }

            // Newtonsoft.Json vulnerabilities
            Add("Newtonsoft.Json", "CVE-2024-21907", VulnSeverity.High,
                "Newtonsoft.Json before 13.0.1 is vulnerable to improper handling of exceptional conditions when serializing or deserializing JSON.",
                ">=0.0.0,<13.0.1", "13.0.1",
                new DateTimeOffset(2024, 1, 3, 0, 0, 0, TimeSpan.Zero),
                "https://github.com/JamesNK/Newtonsoft.Json/issues/2457");

            // System.Text.Json vulnerabilities
            Add("System.Text.Json", "CVE-2024-43485", VulnSeverity.High,
                "System.Text.Json denial of service vulnerability when parsing deeply nested JSON.",
                ">=6.0.0,<8.0.5", "8.0.5",
                new DateTimeOffset(2024, 10, 8, 0, 0, 0, TimeSpan.Zero),
                "https://github.com/dotnet/runtime/issues/108843");

            // System.Security.Cryptography.Xml
            Add("System.Security.Cryptography.Xml", "CVE-2024-43483", VulnSeverity.High,
                "Denial of Service vulnerability in System.Security.Cryptography.Xml.",
                ">=6.0.0,<8.0.1", "8.0.1",
                new DateTimeOffset(2024, 10, 8, 0, 0, 0, TimeSpan.Zero),
                "https://github.com/dotnet/runtime/issues/108844");

            // BouncyCastle.Cryptography
            Add("BouncyCastle.Cryptography", "CVE-2024-29857", VulnSeverity.Medium,
                "Bouncy Castle denial of service through Ed25519 signature verification.",
                ">=2.0.0,<2.3.1", "2.3.1",
                new DateTimeOffset(2024, 6, 14, 0, 0, 0, TimeSpan.Zero),
                "https://github.com/bcgit/bc-csharp/issues/509");

            // SharpCompress
            Add("SharpCompress", "CVE-2024-40624", VulnSeverity.High,
                "SharpCompress directory traversal vulnerability allowing file writes outside target directory.",
                ">=0.0.0,<0.38.0", "0.38.0",
                new DateTimeOffset(2024, 7, 22, 0, 0, 0, TimeSpan.Zero),
                "https://github.com/adamhathcock/sharpcompress/security/advisories/GHSA-jp7f-grcv-6mjf");

            // Npgsql
            Add("Npgsql", "CVE-2024-32655", VulnSeverity.High,
                "Npgsql SQL injection via protocol message size overflow.",
                ">=4.0.0,<8.0.3", "8.0.3",
                new DateTimeOffset(2024, 5, 8, 0, 0, 0, TimeSpan.Zero),
                "https://github.com/npgsql/npgsql/security/advisories/GHSA-x9vc-6hfv-hg8c");

            // MySqlConnector
            Add("MySqlConnector", "CVE-2024-33844", VulnSeverity.Medium,
                "MySqlConnector fails to validate SSL server certificate by default.",
                ">=2.0.0,<2.3.6", "2.3.6",
                new DateTimeOffset(2024, 4, 28, 0, 0, 0, TimeSpan.Zero),
                "https://github.com/mysql-net/MySqlConnector/issues/1340");

            // Microsoft.Data.SqlClient
            Add("Microsoft.Data.SqlClient", "CVE-2024-0056", VulnSeverity.Critical,
                "Microsoft.Data.SqlClient information disclosure through TLS downgrade.",
                ">=1.0.0,<5.1.4", "5.1.4",
                new DateTimeOffset(2024, 1, 9, 0, 0, 0, TimeSpan.Zero),
                "https://msrc.microsoft.com/update-guide/vulnerability/CVE-2024-0056");

            // Azure.Identity
            Add("Azure.Identity", "CVE-2024-35255", VulnSeverity.Medium,
                "Azure Identity Library elevation of privilege vulnerability.",
                ">=1.0.0,<1.11.4", "1.11.4",
                new DateTimeOffset(2024, 6, 11, 0, 0, 0, TimeSpan.Zero),
                "https://github.com/Azure/azure-sdk-for-net/issues/44581");

            // StackExchange.Redis
            Add("StackExchange.Redis", "CVE-2024-21862", VulnSeverity.Medium,
                "StackExchange.Redis connection string injection vulnerability.",
                ">=2.0.0,<2.7.10", "2.7.10",
                new DateTimeOffset(2024, 1, 12, 0, 0, 0, TimeSpan.Zero),
                "https://github.com/StackExchange/StackExchange.Redis/security/advisories/GHSA-hfmf-q69j-6m5p");

            // System.Net.Http
            Add("System.Net.Http", "CVE-2023-36799", VulnSeverity.High,
                "Denial of Service vulnerability when processing X.509 certificates.",
                ">=6.0.0,<8.0.0", "8.0.0",
                new DateTimeOffset(2023, 9, 12, 0, 0, 0, TimeSpan.Zero),
                "https://github.com/dotnet/runtime/issues/91896");

            // MQTTnet
            Add("MQTTnet", "CVE-2023-48310", VulnSeverity.Medium,
                "MQTTnet denial of service through malformed CONNECT packets.",
                ">=4.0.0,<4.3.3", "4.3.3",
                new DateTimeOffset(2023, 11, 15, 0, 0, 0, TimeSpan.Zero),
                "https://github.com/dotnet/MQTTnet/security/advisories/GHSA-7h26-63m7-qhf2");

            // Grpc.Net.Client
            Add("Grpc.Net.Client", "CVE-2023-44487", VulnSeverity.High,
                "HTTP/2 Rapid Reset denial of service vulnerability.",
                ">=2.0.0,<2.59.0", "2.59.0",
                new DateTimeOffset(2023, 10, 10, 0, 0, 0, TimeSpan.Zero),
                "https://github.com/grpc/grpc-dotnet/pull/2291");

            // SSH.NET
            Add("SSH.NET", "CVE-2024-43498", VulnSeverity.Critical,
                "SSH.NET pre-authentication remote code execution.",
                ">=2020.0.0,<2024.1.0", "2024.1.0",
                new DateTimeOffset(2024, 11, 12, 0, 0, 0, TimeSpan.Zero),
                "https://github.com/sshnet/SSH.NET/security/advisories/GHSA-cqj2-g5c8-rcx2");

            // DnsClient.NET
            Add("DnsClient", "CVE-2024-35180", VulnSeverity.Medium,
                "DnsClient.NET cache poisoning vulnerability.",
                ">=1.0.0,<1.8.0", "1.8.0",
                new DateTimeOffset(2024, 5, 14, 0, 0, 0, TimeSpan.Zero),
                "https://github.com/MichaCo/DnsClient.NET/issues/195");

            // FluentValidation
            Add("FluentValidation", "CVE-2023-49283", VulnSeverity.Medium,
                "FluentValidation ReDoS in email validation.",
                ">=0.0.0,<11.8.1", "11.8.1",
                new DateTimeOffset(2023, 11, 29, 0, 0, 0, TimeSpan.Zero),
                "https://github.com/FluentValidation/FluentValidation/security/advisories/GHSA-h52j-3gr3-fg38");

            return db;
        }

        private sealed class KnownVulnerability
        {
            public string CveId { get; init; } = string.Empty;
            public VulnSeverity Severity { get; init; }
            public string Description { get; init; } = string.Empty;
            public string AffectedVersionRange { get; init; } = string.Empty;
            public string? FixedInVersion { get; init; }
            public DateTimeOffset? Published { get; init; }
            public IReadOnlyList<Uri> References { get; init; } = Array.Empty<Uri>();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Online vulnerability database clients
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Queries the OSV.dev API for known vulnerabilities affecting NuGet packages.
    /// No authentication required. Timeout 10s per query, fail-open.
    /// </summary>
    public sealed class OsvClient : IVulnerabilityDatabase
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<OsvClient>? _logger;
        private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Creates a new OSV.dev client.
        /// </summary>
        public OsvClient(HttpClient httpClient, ILogger<OsvClient>? logger = null)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<VulnerabilityFinding>> QueryAsync(
            string packageName, string version, CancellationToken ct = default)
        {
            var findings = new List<VulnerabilityFinding>();

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(QueryTimeout);

                var requestBody = JsonSerializer.Serialize(new
                {
                    version,
                    package_ = new { name = packageName, ecosystem = "NuGet" }
                });

                // Fix: the actual JSON field is "package" not "package_"
                requestBody = requestBody.Replace("\"package_\"", "\"package\"");

                using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                using var response = await _httpClient
                    .PostAsync("https://api.osv.dev/v1/query", content, timeoutCts.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger?.LogDebug("OSV API returned {StatusCode} for {Package}@{Version}",
                        response.StatusCode, packageName, version);
                    return findings;
                }

                var json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("vulns", out var vulns))
                    return findings;

                foreach (var vuln in vulns.EnumerateArray())
                {
                    var id = vuln.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    var summary = vuln.TryGetProperty("summary", out var sumEl) ? sumEl.GetString() ?? "" : "";

                    var severity = VulnSeverity.Medium;
                    if (vuln.TryGetProperty("database_specific", out var dbSpec) &&
                        dbSpec.TryGetProperty("severity", out var sevEl))
                    {
                        severity = ParseSeverity(sevEl.GetString());
                    }

                    var references = new List<Uri>();
                    if (vuln.TryGetProperty("references", out var refsArr))
                    {
                        foreach (var refItem in refsArr.EnumerateArray())
                        {
                            if (refItem.TryGetProperty("url", out var urlEl) &&
                                Uri.TryCreate(urlEl.GetString(), UriKind.Absolute, out var uri))
                            {
                                references.Add(uri);
                            }
                        }
                    }

                    findings.Add(new VulnerabilityFinding
                    {
                        PackageName = packageName,
                        PackageVersion = version,
                        CveId = id,
                        Severity = severity,
                        Description = summary,
                        References = references
                    });
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger?.LogDebug("OSV query timed out for {Package}@{Version}", packageName, version);
            }
            catch (Exception ex)
            {
                // Fail open: missing data = no finding, not error
                _logger?.LogDebug(ex, "OSV query failed for {Package}@{Version}", packageName, version);
            }

            return findings;
        }

        private static VulnSeverity ParseSeverity(string? value) => value?.ToUpperInvariant() switch
        {
            "CRITICAL" => VulnSeverity.Critical,
            "HIGH" => VulnSeverity.High,
            "MEDIUM" or "MODERATE" => VulnSeverity.Medium,
            "LOW" => VulnSeverity.Low,
            _ => VulnSeverity.Medium
        };
    }

    /// <summary>
    /// Queries the GitHub Advisory Database for known vulnerabilities affecting NuGet packages.
    /// Uses the public (no-auth) endpoint. Timeout 10s per query, fail-open.
    /// </summary>
    public sealed class GitHubAdvisoryClient : IVulnerabilityDatabase
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<GitHubAdvisoryClient>? _logger;
        private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Creates a new GitHub Advisory Database client.
        /// </summary>
        public GitHubAdvisoryClient(HttpClient httpClient, ILogger<GitHubAdvisoryClient>? logger = null)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<VulnerabilityFinding>> QueryAsync(
            string packageName, string version, CancellationToken ct = default)
        {
            var findings = new List<VulnerabilityFinding>();

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(QueryTimeout);

                var url = $"https://api.github.com/advisories?affects={Uri.EscapeDataString(packageName)}&ecosystem=nuget";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Accept", "application/vnd.github+json");
                request.Headers.Add("User-Agent", "DataWarehouse-DependencyScanner");

                using var response = await _httpClient
                    .SendAsync(request, timeoutCts.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger?.LogDebug("GitHub Advisory API returned {StatusCode} for {Package}",
                        response.StatusCode, packageName);
                    return findings;
                }

                var json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                foreach (var advisory in doc.RootElement.EnumerateArray())
                {
                    var cveId = advisory.TryGetProperty("cve_id", out var cveEl)
                        ? cveEl.GetString() ?? ""
                        : "";
                    var summary = advisory.TryGetProperty("summary", out var sumEl)
                        ? sumEl.GetString() ?? ""
                        : "";
                    var severityStr = advisory.TryGetProperty("severity", out var sevEl)
                        ? sevEl.GetString()
                        : null;

                    var references = new List<Uri>();
                    if (advisory.TryGetProperty("references", out var refsArr))
                    {
                        foreach (var refItem in refsArr.EnumerateArray())
                        {
                            var refUrl = refItem.GetString();
                            if (refUrl != null && Uri.TryCreate(refUrl, UriKind.Absolute, out var uri))
                            {
                                references.Add(uri);
                            }
                        }
                    }

                    findings.Add(new VulnerabilityFinding
                    {
                        PackageName = packageName,
                        PackageVersion = version,
                        CveId = cveId,
                        Severity = ParseSeverity(severityStr),
                        Description = summary,
                        References = references
                    });
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger?.LogDebug("GitHub Advisory query timed out for {Package}@{Version}",
                    packageName, version);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "GitHub Advisory query failed for {Package}@{Version}",
                    packageName, version);
            }

            return findings;
        }

        private static VulnSeverity ParseSeverity(string? value) => value?.ToUpperInvariant() switch
        {
            "CRITICAL" => VulnSeverity.Critical,
            "HIGH" => VulnSeverity.High,
            "MEDIUM" or "MODERATE" => VulnSeverity.Medium,
            "LOW" => VulnSeverity.Low,
            _ => VulnSeverity.Medium
        };
    }

    // ──────────────────────────────────────────────────────────────
    // Main dependency scanner
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Dependency vulnerability scanner that checks runtime components against known CVE databases.
    /// Supports offline (embedded DB) and online (OSV.dev, GitHub Advisory) scanning modes.
    /// Rate-limited to 10 queries/sec for external APIs. Results cached for 24h by default.
    /// Publishes scan results to message bus topic "security.supply-chain.scan-complete".
    /// Exposes IHealthCheck returning Degraded if High/Critical vulnerabilities found.
    /// </summary>
    public sealed class DependencyScanner : IDependencyScanner, IHealthCheck, IScanScheduler
    {
        private readonly ISbomProvider _sbomProvider;
        private readonly IVulnerabilityDatabase _offlineDb;
        private readonly IReadOnlyList<IVulnerabilityDatabase> _onlineDbs;
        private readonly IMessageBus? _messageBus;
        private readonly ILogger<DependencyScanner>? _logger;

        private readonly SemaphoreSlim _rateLimiter = new(10, 10);
        private readonly BoundedDictionary<string, (DependencyScanResult Result, DateTimeOffset CachedAt)> _cache = new BoundedDictionary<string, (DependencyScanResult Result, DateTimeOffset CachedAt)>(1000);
        private Timer? _scanTimer;
        private DependencyScanResult? _lastScanResult;

        /// <summary>Cache duration for scan results. Default: 24 hours.</summary>
        public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(24);

        /// <summary>Whether to use online vulnerability databases in addition to offline. Default: false.</summary>
        public bool UseOnlineDatabases { get; set; }

        /// <inheritdoc />
        public TimeSpan ScanInterval { get; set; } = TimeSpan.FromHours(24);

        /// <inheritdoc />
        public string Name => "SupplyChainDependencyScanner";

        /// <inheritdoc />
        public IEnumerable<string> Tags => new[] { "security", "supply-chain", "dependencies" };

        /// <summary>
        /// Creates a new dependency scanner.
        /// </summary>
        /// <param name="sbomProvider">SBOM provider for component discovery.</param>
        /// <param name="messageBus">Optional message bus for publishing scan results.</param>
        /// <param name="onlineDbs">Optional online vulnerability databases (OSV, GitHub).</param>
        /// <param name="logger">Optional logger.</param>
        public DependencyScanner(
            ISbomProvider sbomProvider,
            IMessageBus? messageBus = null,
            IReadOnlyList<IVulnerabilityDatabase>? onlineDbs = null,
            ILogger<DependencyScanner>? logger = null)
        {
            _sbomProvider = sbomProvider ?? throw new ArgumentNullException(nameof(sbomProvider));
            _messageBus = messageBus;
            _offlineDb = new EmbeddedVulnerabilityDatabase();
            _onlineDbs = onlineDbs ?? Array.Empty<IVulnerabilityDatabase>();
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<DependencyScanResult> ScanAsync(CancellationToken ct = default)
        {
            var sbom = await _sbomProvider
                .GenerateAsync(SbomFormat.CycloneDX_1_5_Json, null, ct)
                .ConfigureAwait(false);

            return await ScanAsync(sbom, ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<DependencyScanResult> ScanAsync(SbomDocument sbom, CancellationToken ct = default)
        {
            var cacheKey = $"{sbom.ComponentCount}_{sbom.GeneratedAt.Ticks}";
            if (_cache.TryGetValue(cacheKey, out var cached) &&
                DateTimeOffset.UtcNow - cached.CachedAt < CacheDuration)
            {
                return cached.Result;
            }

            _logger?.LogInformation("Starting dependency vulnerability scan for {Count} components",
                sbom.ComponentCount);

            var allFindings = new List<VulnerabilityFinding>();

            foreach (var component in sbom.Components)
            {
                ct.ThrowIfCancellationRequested();

                // Always check offline DB
                var offlineFindings = await _offlineDb
                    .QueryAsync(component.Name, component.Version, ct)
                    .ConfigureAwait(false);
                allFindings.AddRange(offlineFindings);

                // Optionally check online DBs with rate limiting
                if (UseOnlineDatabases && _onlineDbs.Count > 0)
                {
                    await _rateLimiter.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        foreach (var onlineDb in _onlineDbs)
                        {
                            var onlineFindings = await onlineDb
                                .QueryAsync(component.Name, component.Version, ct)
                                .ConfigureAwait(false);

                            // Deduplicate by CVE ID
                            foreach (var finding in onlineFindings)
                            {
                                if (!allFindings.Any(f =>
                                        string.Equals(f.CveId, finding.CveId, StringComparison.OrdinalIgnoreCase) &&
                                        string.Equals(f.PackageName, finding.PackageName, StringComparison.OrdinalIgnoreCase)))
                                {
                                    allFindings.Add(finding);
                                }
                            }
                        }
                    }
                    finally
                    {
                        // Release after a short delay to maintain rate limit
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                            _rateLimiter.Release();
                        }, CancellationToken.None);
                    }
                }
            }

            var result = new DependencyScanResult
            {
                ScannedAt = DateTimeOffset.UtcNow,
                ComponentsScanned = sbom.ComponentCount,
                VulnerabilitiesFound = allFindings.Count,
                Vulnerabilities = allFindings
                    .OrderByDescending(v => v.Severity)
                    .ThenBy(v => v.PackageName)
                    .ToList()
            };

            _lastScanResult = result;
            _cache[cacheKey] = (result, DateTimeOffset.UtcNow);

            _logger?.LogInformation(
                "Dependency scan complete: {Scanned} components, {Found} vulnerabilities ({Critical}C/{High}H/{Medium}M/{Low}L)",
                result.ComponentsScanned, result.VulnerabilitiesFound,
                result.CriticalCount, result.HighCount, result.MediumCount, result.LowCount);

            // Publish scan results to message bus
            if (_messageBus != null)
            {
                try
                {
                    await _messageBus.PublishAsync(
                        "security.supply-chain.scan-complete",
                        new PluginMessage
                        {
                            Type = "security.supply-chain.scan-complete",
                            SourcePluginId = "DataWarehouse.SDK.Security.SupplyChain",
                            Source = "DependencyScanner",
                            Payload = new Dictionary<string, object>
                            {
                                ["scannedAt"] = result.ScannedAt.ToString("o"),
                                ["componentsScanned"] = result.ComponentsScanned,
                                ["vulnerabilitiesFound"] = result.VulnerabilitiesFound,
                                ["criticalCount"] = result.CriticalCount,
                                ["highCount"] = result.HighCount,
                                ["mediumCount"] = result.MediumCount,
                                ["lowCount"] = result.LowCount,
                                ["hasHighOrCritical"] = result.HasHighOrCritical
                            },
                            Description = $"Supply chain scan complete: {result.VulnerabilitiesFound} vulnerabilities found in {result.ComponentsScanned} components"
                        },
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to publish scan results to message bus");
                }
            }

            return result;
        }

        // ──────────────────────────────────────────────────────────────
        // IHealthCheck implementation
        // ──────────────────────────────────────────────────────────────

        /// <inheritdoc />
        public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
        {
            if (_lastScanResult == null)
            {
                return Task.FromResult(HealthCheckResult.Healthy(
                    "Supply chain scanner initialized, no scan performed yet."));
            }

            var data = new Dictionary<string, object>
            {
                ["componentsScanned"] = _lastScanResult.ComponentsScanned,
                ["vulnerabilitiesFound"] = _lastScanResult.VulnerabilitiesFound,
                ["criticalCount"] = _lastScanResult.CriticalCount,
                ["highCount"] = _lastScanResult.HighCount,
                ["lastScanAt"] = _lastScanResult.ScannedAt.ToString("o")
            };

            if (_lastScanResult.HasHighOrCritical)
            {
                return Task.FromResult(new HealthCheckResult
                {
                    Status = HealthStatus.Degraded,
                    Message = $"Supply chain: {_lastScanResult.CriticalCount} critical, " +
                              $"{_lastScanResult.HighCount} high severity vulnerabilities detected",
                    Data = data
                });
            }

            if (_lastScanResult.VulnerabilitiesFound > 0)
            {
                return Task.FromResult(new HealthCheckResult
                {
                    Status = HealthStatus.Healthy,
                    Message = $"Supply chain: {_lastScanResult.VulnerabilitiesFound} low/medium vulnerabilities " +
                              $"(no critical/high)",
                    Data = data
                });
            }

            return Task.FromResult(new HealthCheckResult
            {
                Status = HealthStatus.Healthy,
                Message = $"Supply chain: {_lastScanResult.ComponentsScanned} components scanned, no vulnerabilities",
                Data = data
            });
        }

        // ──────────────────────────────────────────────────────────────
        // IScanScheduler implementation
        // ──────────────────────────────────────────────────────────────

        /// <inheritdoc />
        public Task StartAsync(CancellationToken ct = default)
        {
            _scanTimer?.Dispose();
            _scanTimer = new Timer(
                async _ =>
                {
                    try
                    {
                        await ScanAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Scheduled dependency scan failed");
                    }
                },
                null,
                TimeSpan.Zero, // Run immediately on start
                ScanInterval);

            _logger?.LogInformation("Dependency scan scheduler started with interval {Interval}", ScanInterval);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken ct = default)
        {
            _scanTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _logger?.LogInformation("Dependency scan scheduler stopped");
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _scanTimer?.Dispose();
            _rateLimiter.Dispose();
        }
    }
}
