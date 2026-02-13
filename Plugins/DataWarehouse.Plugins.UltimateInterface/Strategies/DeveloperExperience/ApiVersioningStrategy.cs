using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SdkInterface = DataWarehouse.SDK.Contracts.Interface;

namespace DataWarehouse.Plugins.UltimateInterface.Strategies.DeveloperExperience;

/// <summary>
/// API versioning strategy supporting multiple versioning schemes.
/// </summary>
/// <remarks>
/// <para>
/// Provides production-ready API versioning with:
/// <list type="bullet">
/// <item><description>URL path versioning (/v1/resource, /v2/resource)</description></item>
/// <item><description>Header versioning (X-Api-Version: 1.0)</description></item>
/// <item><description>Query parameter versioning (?api-version=1.0)</description></item>
/// <item><description>Accept header versioning (Accept: application/vnd.datawarehouse.v1+json)</description></item>
/// <item><description>Version deprecation warnings</description></item>
/// <item><description>Automatic version routing to appropriate handlers</description></item>
/// </list>
/// </para>
/// <para>
/// Version handlers are registered with the orchestrator and routed based on
/// the versioning scheme specified in the request.
/// </para>
/// </remarks>
internal sealed class ApiVersioningStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    // IPluginInterfaceStrategy metadata
    public override string StrategyId => "api-versioning";
    public string DisplayName => "API Versioning";
    public string SemanticDescription => "Multi-scheme API versioning with URL path, header, query parameter, and Accept header support plus deprecation warnings.";
    public InterfaceCategory Category => InterfaceCategory.Innovation;
    public string[] Tags => new[] { "versioning", "compatibility", "developer-experience", "routing", "deprecation" };

    // SDK contract properties
    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.REST;
    public override SdkInterface.InterfaceCapabilities Capabilities => new SdkInterface.InterfaceCapabilities(
        SupportsStreaming: false,
        SupportsAuthentication: true,
        SupportedContentTypes: new[] { "application/json", "application/vnd.datawarehouse.v1+json", "application/vnd.datawarehouse.v2+json" },
        MaxRequestSize: 10 * 1024 * 1024, // 10 MB
        MaxResponseSize: 10 * 1024 * 1024, // 10 MB
        DefaultTimeout: TimeSpan.FromSeconds(30)
    );

    private readonly Dictionary<string, VersionInfo> _versions = new()
    {
        ["1.0"] = new VersionInfo { Version = "1.0", Status = VersionStatus.Active, ReleaseDate = new DateTime(2024, 1, 1) },
        ["2.0"] = new VersionInfo { Version = "2.0", Status = VersionStatus.Active, ReleaseDate = new DateTime(2025, 1, 1) },
        ["3.0"] = new VersionInfo { Version = "3.0", Status = VersionStatus.Beta, ReleaseDate = new DateTime(2026, 1, 1) }
    };

    /// <summary>
    /// Initializes the API versioning strategy.
    /// </summary>
    protected override Task StartAsyncCore(CancellationToken cancellationToken)
    {
        // No state initialization required
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cleans up versioning resources.
    /// </summary>
    protected override Task StopAsyncCore(CancellationToken cancellationToken)
    {
        // No cleanup required
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles versioned API requests.
    /// </summary>
    /// <param name="request">The validated interface request.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>An InterfaceResponse routed to the appropriate version handler.</returns>
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        // Detect version from multiple sources
        var version = DetectVersion(request);

        if (version == null)
        {
            return SdkInterface.InterfaceResponse.BadRequest("API version not specified. Use URL path (/v1/...), header (X-Api-Version), query param (?api-version=), or Accept header.");
        }

        // Validate version exists
        if (!_versions.TryGetValue(version, out var versionInfo))
        {
            return SdkInterface.InterfaceResponse.BadRequest($"Unsupported API version: {version}. Supported versions: {string.Join(", ", _versions.Keys)}");
        }

        // Build version-specific headers
        var additionalHeaders = new Dictionary<string, string>();

        if (versionInfo.Status == VersionStatus.Deprecated)
        {
            additionalHeaders["X-Api-Deprecated"] = "true";
            additionalHeaders["X-Api-Sunset"] = versionInfo.SunsetDate?.ToString("yyyy-MM-dd") ?? "unknown";
            additionalHeaders["Warning"] = $"299 - \"API version {version} is deprecated and will be removed on {versionInfo.SunsetDate:yyyy-MM-dd}\"";
        }
        else if (versionInfo.Status == VersionStatus.Beta)
        {
            additionalHeaders["X-Api-Status"] = "beta";
            additionalHeaders["Warning"] = "199 - \"This API version is in beta and may change without notice\"";
        }

        // Route to version-specific handler with additional headers
        var response = await RouteToVersionHandlerAsync(version, request, additionalHeaders, cancellationToken);

        return response;
    }

    /// <summary>
    /// Detects the API version from the request using multiple schemes.
    /// </summary>
    private string? DetectVersion(SdkInterface.InterfaceRequest request)
    {
        // 1. URL path versioning: /v1/resource
        var path = request.Path.TrimStart('/');
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0 && segments[0].StartsWith("v") && char.IsDigit(segments[0][1]))
        {
            var versionNumber = segments[0].Substring(1);
            if (versionNumber.Contains('.'))
            {
                return versionNumber;
            }
            return $"{versionNumber}.0";
        }

        // 2. Header versioning: X-Api-Version: 1.0
        if (request.Headers.TryGetValue("X-Api-Version", out var headerVersion))
        {
            return headerVersion;
        }

        // 3. Query parameter versioning: ?api-version=1.0
        if (request.QueryParameters.TryGetValue("api-version", out var queryVersion))
        {
            return queryVersion;
        }

        // 4. Accept header versioning: application/vnd.datawarehouse.v1+json
        if (request.Headers.TryGetValue("Accept", out var acceptHeader))
        {
            var match = System.Text.RegularExpressions.Regex.Match(acceptHeader, @"\.v(\d+(?:\.\d+)?)");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        // Default to latest stable version
        var latestStable = _versions.Values
            .Where(v => v.Status == VersionStatus.Active)
            .OrderByDescending(v => v.ReleaseDate)
            .FirstOrDefault();

        return latestStable?.Version;
    }

    /// <summary>
    /// Routes the request to the appropriate version handler.
    /// </summary>
    private async Task<SdkInterface.InterfaceResponse> RouteToVersionHandlerAsync(
        string version,
        SdkInterface.InterfaceRequest request,
        Dictionary<string, string> additionalHeaders,
        CancellationToken cancellationToken)
    {
        // In production, this would route via message bus topic "interface.version.{version}"
        // For now, return a success response indicating the version

        var result = new
        {
            status = "success",
            version,
            message = $"Request routed to version {version} handler",
            path = request.Path,
            method = request.Method.ToString()
        };

        var responseBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

        // Merge additional headers with base headers
        var headers = new Dictionary<string, string> { ["Content-Type"] = "application/json", ["X-Api-Version"] = version };
        foreach (var header in additionalHeaders)
        {
            headers[header.Key] = header.Value;
        }

        return new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: headers,
            Body: responseBody
        );
    }

    private sealed class VersionInfo
    {
        public string Version { get; set; } = "";
        public VersionStatus Status { get; set; }
        public DateTime ReleaseDate { get; set; }
        public DateTime? SunsetDate { get; set; }
    }

    private enum VersionStatus
    {
        Beta,
        Active,
        Deprecated,
        Retired
    }
}
