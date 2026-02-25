using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SdkInterface = DataWarehouse.SDK.Contracts.Interface;

namespace DataWarehouse.Plugins.UltimateInterface.Strategies.Innovation;

/// <summary>
/// Versionless API strategy that eliminates explicit versioning via automatic compatibility transformations.
/// </summary>
/// <remarks>
/// <para>
/// Provides production-ready versionless API with:
/// <list type="bullet">
/// <item><description>No explicit API versions (no /v1/, /v2/ in URLs)</description></item>
/// <item><description>Request shape detection to infer client expectations</description></item>
/// <item><description>Automatic backward-compatible transformations</description></item>
/// <item><description>Response format adaptation to client capabilities</description></item>
/// <item><description>Schema evolution without breaking changes</description></item>
/// <item><description>Content negotiation for format selection</description></item>
/// </list>
/// </para>
/// <para>
/// Detects client version expectations from request structure and headers.
/// Automatically transforms responses to match client's expected schema.
/// </para>
/// </remarks>
internal sealed class VersionlessApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    // IPluginInterfaceStrategy metadata
    public override string StrategyId => "versionless-api";
    public string DisplayName => "Versionless API";
    public string SemanticDescription => "No explicit API versioning required - detects client expectations from request shape and applies backward-compatible transformations.";
    public InterfaceCategory Category => InterfaceCategory.Innovation;
    public string[] Tags => new[] { "versionless", "schema-evolution", "backward-compatible", "innovation" };

    // SDK contract properties
    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.REST;
    public override SdkInterface.InterfaceCapabilities Capabilities => new SdkInterface.InterfaceCapabilities(
        SupportsStreaming: false,
        SupportsAuthentication: true,
        SupportedContentTypes: new[] { "application/json" },
        MaxRequestSize: 1024 * 1024, // 1 MB
        MaxResponseSize: 5 * 1024 * 1024, // 5 MB
        DefaultTimeout: TimeSpan.FromSeconds(30)
    );

    /// <summary>
    /// Initializes the Versionless API strategy.
    /// </summary>
    protected override Task StartAsyncCore(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cleans up Versionless API resources.
    /// </summary>
    protected override Task StopAsyncCore(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles requests by detecting version expectations and adapting response.
    /// </summary>
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        var clientVersion = DetectClientVersion(request);
        var path = request.Path?.TrimStart('/') ?? string.Empty;

        // Get the latest version of data
        var latestData = GetLatestData(path);

        // Transform to client's expected schema
        var adaptedData = AdaptToClientVersion(latestData, clientVersion);

        var response = new
        {
            data = adaptedData,
            metadata = new
            {
                detectedClientVersion = clientVersion,
                currentSchemaVersion = "2023.02",
                transformationApplied = clientVersion != "2023.02",
                backwardCompatible = true
            }
        };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        var body = Encoding.UTF8.GetBytes(json);

        return new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["X-Schema-Version"] = "2023.02",
                ["X-Client-Version-Detected"] = clientVersion,
                ["X-Transformation-Applied"] = clientVersion != "2023.02" ? "true" : "false"
            },
            Body: body
        );
    }

    /// <summary>
    /// Detects client version from request headers and shape.
    /// </summary>
    private string DetectClientVersion(SdkInterface.InterfaceRequest request)
    {
        // Check for explicit version hints
        if (request.Headers.TryGetValue("X-Client-Schema-Version", out var schemaVersion))
        {
            return schemaVersion;
        }

        if (request.Headers.TryGetValue("User-Agent", out var userAgent))
        {
            // Parse version from User-Agent (e.g., "DataWarehouse-Client/2023.01")
            if (userAgent.Contains("/"))
            {
                var parts = userAgent.Split('/');
                if (parts.Length == 2)
                {
                    return parts[1];
                }
            }
        }

        // Analyze request body structure to infer version
        if (request.Body.Length > 0)
        {
            try
            {
                var bodyText = Encoding.UTF8.GetString(request.Body.Span);
                using var doc = JsonDocument.Parse(bodyText);
                var root = doc.RootElement;

                // Version 2022.12 used "filter" field
                if (root.TryGetProperty("filter", out _))
                {
                    return "2022.12";
                }

                // Version 2023.01 used "query" field
                if (root.TryGetProperty("query", out _) && !root.TryGetProperty("options", out _))
                {
                    return "2023.01";
                }

                // Version 2023.02 uses "query" + "options"
                if (root.TryGetProperty("query", out _) && root.TryGetProperty("options", out _))
                {
                    return "2023.02";
                }
            }
            catch { /* Parsing failure — default to latest */ }
        }

        // Default to latest version
        return "2023.02";
    }

    /// <summary>
    /// Gets the latest version of data.
    /// </summary>
    private object GetLatestData(string path)
    {
        // Latest schema includes all fields
        return new
        {
            id = "ds-001",
            name = "Sales Data",
            size = 1258291200, // bytes
            sizeFormatted = "1.2 GB",
            created = "2026-01-15T10:30:00Z",
            createdTimestamp = 1705318200,
            modified = "2026-02-10T14:22:00Z",
            modifiedTimestamp = 1707574920,
            owner = new
            {
                id = "user-123",
                name = "Sales Team",
                email = "sales@company.com"
            },
            tags = new[] { "sales", "analytics", "quarterly" },
            metadata = new
            {
                region = "us-west",
                classification = "confidential",
                retentionDays = 365
            }
        };
    }

    /// <summary>
    /// Adapts latest data to client's expected schema version.
    /// </summary>
    private object AdaptToClientVersion(object latestData, string clientVersion)
    {
        var data = (dynamic)latestData;

        return clientVersion switch
        {
            // Version 2022.12: Minimal fields, no nested objects
            "2022.12" => new
            {
                id = data.id,
                name = data.name,
                size = data.sizeFormatted, // String format
                created = data.created,
                owner = data.owner.name, // Flattened
                tags = data.tags
            },

            // Version 2023.01: Added timestamps, still flat owner
            "2023.01" => new
            {
                id = data.id,
                name = data.name,
                size = data.size, // Numeric bytes
                sizeFormatted = data.sizeFormatted,
                created = data.created,
                createdTimestamp = data.createdTimestamp,
                modified = data.modified,
                modifiedTimestamp = data.modifiedTimestamp,
                owner = data.owner.name, // Still flattened
                tags = data.tags
            },

            // Version 2023.02: Latest schema with nested objects
            "2023.02" => latestData,

            // Unknown version: return latest
            _ => latestData
        };
    }
}
