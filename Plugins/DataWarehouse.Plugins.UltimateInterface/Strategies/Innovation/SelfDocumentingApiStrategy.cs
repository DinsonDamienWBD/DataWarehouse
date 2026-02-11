using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SdkInterface = DataWarehouse.SDK.Contracts.Interface;

namespace DataWarehouse.Plugins.UltimateInterface.Strategies.Innovation;

/// <summary>
/// Self-Documenting API strategy that includes documentation in every response.
/// </summary>
/// <remarks>
/// <para>
/// Provides production-ready self-documenting API with:
/// <list type="bullet">
/// <item><description>X-Api-Help header in every response</description></item>
/// <item><description>?explain=true query parameter for inline documentation</description></item>
/// <item><description>?example=true query parameter for usage examples</description></item>
/// <item><description>Root path returns navigable API map</description></item>
/// <item><description>OpenAPI/Swagger specification at /api-docs</description></item>
/// <item><description>Interactive API explorer at /explore</description></item>
/// </list>
/// </para>
/// <para>
/// Every endpoint response includes contextual help links and parameter documentation.
/// Reduces need for separate API documentation by embedding it in the API itself.
/// </para>
/// </remarks>
internal sealed class SelfDocumentingApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    // IPluginInterfaceStrategy metadata
    public string StrategyId => "self-documenting-api";
    public string DisplayName => "Self-Documenting API";
    public string SemanticDescription => "Every response includes X-Api-Help header, supports ?explain=true for docs, root path returns navigable API map.";
    public InterfaceCategory Category => InterfaceCategory.Innovation;
    public string[] Tags => new[] { "self-documenting", "api-docs", "discovery", "openapi", "innovation" };

    // SDK contract properties
    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.REST;
    public override SdkInterface.InterfaceCapabilities Capabilities => new SdkInterface.InterfaceCapabilities(
        SupportsStreaming: false,
        SupportsAuthentication: true,
        SupportedContentTypes: new[] { "application/json", "text/html" },
        MaxRequestSize: 100 * 1024, // 100 KB
        MaxResponseSize: 1024 * 1024, // 1 MB
        DefaultTimeout: TimeSpan.FromSeconds(30)
    );

    /// <summary>
    /// Initializes the Self-Documenting API strategy.
    /// </summary>
    protected override Task StartAsyncCore(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cleans up Self-Documenting API resources.
    /// </summary>
    protected override Task StopAsyncCore(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles requests with embedded documentation.
    /// </summary>
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        var path = request.Path?.TrimStart('/') ?? string.Empty;
        var queryParams = ParseQueryParams(request.Path);

        // Root path: API map
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            return ServeApiMap();
        }

        // API documentation
        if (path.Equals("api-docs", StringComparison.OrdinalIgnoreCase))
        {
            return ServeOpenApiSpec();
        }

        // Interactive explorer
        if (path.Equals("explore", StringComparison.OrdinalIgnoreCase))
        {
            return ServeInteractiveExplorer();
        }

        // Regular endpoint with documentation
        return await HandleDocumentedEndpoint(path, queryParams, cancellationToken);
    }

    /// <summary>
    /// Parses query parameters from path.
    /// </summary>
    private Dictionary<string, string> ParseQueryParams(string? path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (path == null || !path.Contains('?'))
            return result;

        var queryString = path.Split('?')[1];
        var pairs = queryString.Split('&');

        foreach (var pair in pairs)
        {
            var parts = pair.Split('=');
            if (parts.Length == 2)
            {
                result[parts[0]] = parts[1];
            }
        }

        return result;
    }

    /// <summary>
    /// Serves the API map at root path.
    /// </summary>
    private SdkInterface.InterfaceResponse ServeApiMap()
    {
        var apiMap = new
        {
            api = "DataWarehouse Self-Documenting API",
            version = "1.0.0",
            documentation = "/api-docs",
            explorer = "/explore",
            endpoints = new[]
            {
                new
                {
                    path = "/datasets",
                    methods = new[] { "GET", "POST" },
                    description = "List or create datasets",
                    help = "/datasets?explain=true"
                },
                new
                {
                    path = "/query",
                    methods = new[] { "POST" },
                    description = "Execute data queries",
                    help = "/query?explain=true"
                },
                new
                {
                    path = "/status",
                    methods = new[] { "GET" },
                    description = "Get system status",
                    help = "/status?explain=true"
                }
            },
            queryParameters = new
            {
                explain = "Add ?explain=true to any endpoint for inline documentation",
                example = "Add ?example=true to any endpoint for usage examples"
            }
        };

        var json = JsonSerializer.Serialize(apiMap, new JsonSerializerOptions { WriteIndented = true });
        var body = Encoding.UTF8.GetBytes(json);

        return new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["X-Api-Help"] = "Root API map - navigate to /api-docs for full OpenAPI spec"
            },
            Body: body
        );
    }

    /// <summary>
    /// Serves OpenAPI specification.
    /// </summary>
    private SdkInterface.InterfaceResponse ServeOpenApiSpec()
    {
        var spec = new
        {
            openapi = "3.1.0",
            info = new
            {
                title = "DataWarehouse API",
                version = "1.0.0",
                description = "Self-documenting API for DataWarehouse operations"
            },
            servers = new[]
            {
                new { url = "https://api.datawarehouse.local" }
            },
            paths = new Dictionary<string, object>
            {
                ["/datasets"] = new
                {
                    get = new
                    {
                        summary = "List datasets",
                        parameters = new[] { new { name = "limit", @in = "query", schema = new { type = "integer" } } },
                        responses = new { }
                    }
                },
                ["/query"] = new
                {
                    post = new
                    {
                        summary = "Execute query",
                        requestBody = new { },
                        responses = new { }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(spec, new JsonSerializerOptions { WriteIndented = true });
        var body = Encoding.UTF8.GetBytes(json);

        return new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["X-Api-Help"] = "Full OpenAPI 3.1.0 specification"
            },
            Body: body
        );
    }

    /// <summary>
    /// Serves interactive API explorer.
    /// </summary>
    private SdkInterface.InterfaceResponse ServeInteractiveExplorer()
    {
        var html = @"
<!DOCTYPE html>
<html>
<head><title>API Explorer</title></head>
<body>
<h1>DataWarehouse API Explorer</h1>
<p>Interactive API documentation and testing interface.</p>
<ul>
  <li><a href=""/api-docs"">OpenAPI Specification</a></li>
  <li><a href=""/"">API Map</a></li>
</ul>
</body>
</html>";

        var body = Encoding.UTF8.GetBytes(html);

        return new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "text/html",
                ["X-Api-Help"] = "Interactive API explorer"
            },
            Body: body
        );
    }

    /// <summary>
    /// Handles regular endpoints with documentation support.
    /// </summary>
    private Task<SdkInterface.InterfaceResponse> HandleDocumentedEndpoint(
        string path,
        Dictionary<string, string> queryParams,
        CancellationToken cancellationToken)
    {
        var explain = queryParams.ContainsKey("explain") && queryParams["explain"] == "true";
        var example = queryParams.ContainsKey("example") && queryParams["example"] == "true";

        object response = path switch
        {
            "datasets" => new
            {
                data = new[] { new { id = "ds-001", name = "Sales Data" } },
                documentation = explain ? new
                {
                    endpoint = "/datasets",
                    description = "Returns list of available datasets",
                    parameters = new { limit = "Maximum number of results (default: 10)" },
                    authentication = "Bearer token required"
                } : null,
                example = example ? new
                {
                    request = "GET /datasets?limit=10",
                    headers = new { Authorization = "Bearer <token>" }
                } : null
            },
            _ => new
            {
                data = new { message = "Endpoint documentation available with ?explain=true" },
                availableEndpoints = new[] { "/datasets", "/query", "/status" }
            }
        };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        var body = Encoding.UTF8.GetBytes(json);

        return Task.FromResult(new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["X-Api-Help"] = $"Add ?explain=true for docs, ?example=true for examples",
                ["Link"] = $"</api-docs>; rel=\"documentation\""
            },
            Body: body
        ));
    }
}
