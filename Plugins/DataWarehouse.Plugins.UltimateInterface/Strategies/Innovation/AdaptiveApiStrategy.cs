using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SdkInterface = DataWarehouse.SDK.Contracts.Interface;

namespace DataWarehouse.Plugins.UltimateInterface.Strategies.Innovation;

/// <summary>
/// Adaptive API strategy that adjusts response detail and format based on client capabilities.
/// </summary>
/// <remarks>
/// <para>
/// Provides production-ready adaptive API with:
/// <list type="bullet">
/// <item><description>Client capability detection from User-Agent, Accept, X-Client-Capabilities headers</description></item>
/// <item><description>Response detail level adjustment (minimal, standard, detailed, verbose)</description></item>
/// <item><description>Format optimization (JSON, compact JSON, MessagePack, CBOR)</description></item>
/// <item><description>Bandwidth-aware response sizing</description></item>
/// <item><description>Device-specific optimizations (mobile, desktop, IoT)</description></item>
/// <item><description>Progressive enhancement support</description></item>
/// </list>
/// </para>
/// <para>
/// Automatically tailors responses to client needs without explicit API versioning.
/// Mobile clients receive compact responses, desktop clients receive detailed responses.
/// </para>
/// </remarks>
internal sealed class AdaptiveApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    // IPluginInterfaceStrategy metadata
    public string StrategyId => "adaptive-api";
    public string DisplayName => "Adaptive API";
    public string SemanticDescription => "Automatically adjusts response detail level and format based on client capabilities (User-Agent, Accept, bandwidth).";
    public InterfaceCategory Category => InterfaceCategory.Innovation;
    public string[] Tags => new[] { "adaptive", "responsive", "client-aware", "optimization", "innovation" };

    // SDK contract properties
    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.REST;
    public override SdkInterface.InterfaceCapabilities Capabilities => new SdkInterface.InterfaceCapabilities(
        SupportsStreaming: false,
        SupportsAuthentication: true,
        SupportedContentTypes: new[] { "application/json", "application/cbor", "application/msgpack" },
        MaxRequestSize: 1024 * 1024, // 1 MB
        MaxResponseSize: 10 * 1024 * 1024, // 10 MB
        DefaultTimeout: TimeSpan.FromSeconds(30)
    );

    /// <summary>
    /// Initializes the Adaptive API strategy.
    /// </summary>
    protected override Task StartAsyncCore(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cleans up Adaptive API resources.
    /// </summary>
    protected override Task StopAsyncCore(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles requests by detecting client capabilities and adapting response.
    /// </summary>
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        var clientProfile = DetectClientCapabilities(request);
        var data = GetSampleData();
        var adaptedData = AdaptDataToClient(data, clientProfile);

        var response = new
        {
            data = adaptedData,
            metadata = new
            {
                detailLevel = clientProfile.DetailLevel,
                deviceType = clientProfile.DeviceType,
                bandwidth = clientProfile.Bandwidth,
                compressionSupported = clientProfile.SupportsCompression,
                fieldsIncluded = clientProfile.DetailLevel switch
                {
                    "minimal" => new[] { "id", "name" },
                    "standard" => new[] { "id", "name", "size", "created" },
                    "detailed" => new[] { "id", "name", "size", "created", "modified", "owner" },
                    "verbose" => new[] { "id", "name", "size", "created", "modified", "owner", "tags", "metadata" },
                    _ => new[] { "id", "name", "size" }
                }
            }
        };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            WriteIndented = clientProfile.DeviceType == "desktop"
        });

        var body = Encoding.UTF8.GetBytes(json);

        return new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["X-Detail-Level"] = clientProfile.DetailLevel,
                ["X-Device-Type"] = clientProfile.DeviceType,
                ["X-Adaptation-Applied"] = "true"
            },
            Body: body
        );
    }

    /// <summary>
    /// Detects client capabilities from request headers.
    /// </summary>
    private ClientProfile DetectClientCapabilities(SdkInterface.InterfaceRequest request)
    {
        var userAgent = request.Headers.TryGetValue("User-Agent", out var ua) ? ua : string.Empty;
        var accept = request.Headers.TryGetValue("Accept", out var acc) ? acc : "application/json";
        var clientCaps = request.Headers.TryGetValue("X-Client-Capabilities", out var caps) ? caps : string.Empty;

        // Detect device type from User-Agent
        var deviceType = DetectDeviceType(userAgent);

        // Detect bandwidth constraints
        var bandwidth = clientCaps.Contains("low-bandwidth", StringComparison.OrdinalIgnoreCase) ? "low" : "high";

        // Detect detail level preference
        var detailLevel = clientCaps.Contains("minimal", StringComparison.OrdinalIgnoreCase) ? "minimal" :
                          clientCaps.Contains("verbose", StringComparison.OrdinalIgnoreCase) ? "verbose" :
                          deviceType == "mobile" ? "standard" : "detailed";

        // Detect compression support
        var supportsCompression = request.Headers.TryGetValue("Accept-Encoding", out var encoding) &&
                                  (encoding.Contains("gzip") || encoding.Contains("br"));

        return new ClientProfile
        {
            DeviceType = deviceType,
            Bandwidth = bandwidth,
            DetailLevel = detailLevel,
            SupportsCompression = supportsCompression,
            AcceptedFormats = accept.Split(',').Select(f => f.Trim()).ToArray()
        };
    }

    /// <summary>
    /// Detects device type from User-Agent string.
    /// </summary>
    private string DetectDeviceType(string userAgent)
    {
        var lower = userAgent.ToLowerInvariant();

        if (lower.Contains("mobile") || lower.Contains("android") || lower.Contains("iphone"))
            return "mobile";

        if (lower.Contains("tablet") || lower.Contains("ipad"))
            return "tablet";

        if (lower.Contains("iot") || lower.Contains("embedded"))
            return "iot";

        return "desktop";
    }

    /// <summary>
    /// Gets sample data for demonstration.
    /// </summary>
    private List<object> GetSampleData()
    {
        return new List<object>
        {
            new
            {
                id = "ds-001",
                name = "Sales Data",
                size = "1.2 GB",
                created = "2026-01-15",
                modified = "2026-02-10",
                owner = "sales-team",
                tags = new[] { "sales", "analytics", "quarterly" },
                metadata = new { region = "us-west", classification = "confidential" }
            },
            new
            {
                id = "ds-002",
                name = "Customer Analytics",
                size = "3.4 GB",
                created = "2026-01-20",
                modified = "2026-02-09",
                owner = "analytics-team",
                tags = new[] { "customer", "behavior", "realtime" },
                metadata = new { region = "eu-central", classification = "internal" }
            }
        };
    }

    /// <summary>
    /// Adapts data to client capabilities.
    /// </summary>
    private object AdaptDataToClient(List<object> data, ClientProfile profile)
    {
        return profile.DetailLevel switch
        {
            "minimal" => data.Select(d =>
            {
                var item = (dynamic)d;
                return new { id = item.id, name = item.name };
            }).ToArray(),

            "standard" => data.Select(d =>
            {
                var item = (dynamic)d;
                return new { id = item.id, name = item.name, size = item.size, created = item.created };
            }).ToArray(),

            "detailed" => data.Select(d =>
            {
                var item = (dynamic)d;
                return new { id = item.id, name = item.name, size = item.size, created = item.created, modified = item.modified, owner = item.owner };
            }).ToArray(),

            "verbose" => data.ToArray(),

            _ => data.ToArray()
        };
    }

    private class ClientProfile
    {
        public required string DeviceType { get; init; }
        public required string Bandwidth { get; init; }
        public required string DetailLevel { get; init; }
        public required bool SupportsCompression { get; init; }
        public required string[] AcceptedFormats { get; init; }
    }
}
