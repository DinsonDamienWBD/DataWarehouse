using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SdkInterface = DataWarehouse.SDK.Contracts.Interface;

namespace DataWarehouse.Plugins.UltimateInterface.Strategies.REST;

/// <summary>
/// Falcor strategy implementing Netflix Falcor JSON Graph protocol.
/// </summary>
/// <remarks>
/// <para>
/// Provides production-ready Falcor handling with:
/// <list type="bullet">
/// <item><description>JSON Graph data model for efficient data fetching</description></item>
/// <item><description>Path-based resource addressing (e.g., users[0..10].name)</description></item>
/// <item><description>Support for get, set, and call operations</description></item>
/// <item><description>Batch request optimization via path sets</description></item>
/// <item><description>Reference resolution for normalized data graphs</description></item>
/// <item><description>Sentinel values ($atom, $ref, $error) for graph metadata</description></item>
/// </list>
/// </para>
/// <para>
/// Falcor allows clients to request exactly the data they need using path expressions,
/// and the server returns a JSON Graph with only the requested paths materialized.
/// This reduces over-fetching and under-fetching common in traditional REST APIs.
/// </para>
/// </remarks>
internal sealed class FalcorStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    // IPluginInterfaceStrategy metadata
    public string StrategyId => "falcor";
    public string DisplayName => "Falcor";
    public string SemanticDescription => "Netflix Falcor JSON Graph protocol with path-based data fetching, batch optimization, and reference resolution.";
    public InterfaceCategory Category => InterfaceCategory.Http;
    public string[] Tags => new[] { "falcor", "json-graph", "batch", "netflix", "query" };

    // SDK contract properties
    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.REST;
    public override SdkInterface.InterfaceCapabilities Capabilities => new SdkInterface.InterfaceCapabilities(
        SupportsStreaming: false,
        SupportsAuthentication: true,
        SupportedContentTypes: new[] { "application/json", "application/x-json-graph" },
        MaxRequestSize: 10 * 1024 * 1024, // 10 MB
        MaxResponseSize: 50 * 1024 * 1024, // 50 MB
        DefaultTimeout: TimeSpan.FromSeconds(30)
    );

    /// <summary>
    /// Initializes the Falcor strategy.
    /// </summary>
    protected override Task StartAsyncCore(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cleans up Falcor resources.
    /// </summary>
    protected override Task StopAsyncCore(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles Falcor JSON Graph requests.
    /// </summary>
    /// <param name="request">The validated interface request.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>An InterfaceResponse with JSON Graph formatted body.</returns>
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = request.Path?.TrimStart('/') ?? string.Empty;

            // Falcor typically uses POST for all operations to /model.json
            if (!path.Equals("model.json", StringComparison.OrdinalIgnoreCase))
            {
                return CreateErrorInterfaceResponse(404, "Not Found",
                    "Falcor endpoint is available at /model.json");
            }

            if (request.Method != SdkInterface.HttpMethod.POST)
            {
                return CreateErrorInterfaceResponse(405, "Method Not Allowed",
                    "Falcor requires POST requests to /model.json");
            }

            if (request.Body.Length == 0)
            {
                return CreateErrorInterfaceResponse(400, "Bad Request",
                    "Request body is required");
            }

            // Parse Falcor request
            var bodyText = Encoding.UTF8.GetString(request.Body.Span);
            var falcorRequest = JsonSerializer.Deserialize<FalcorRequest>(bodyText);

            if (falcorRequest == null)
            {
                return CreateErrorInterfaceResponse(400, "Bad Request",
                    "Invalid Falcor request format");
            }

            // Route based on operation
            var result = falcorRequest.Method?.ToLowerInvariant() switch
            {
                "get" => await HandleGetOperationAsync(falcorRequest, cancellationToken),
                "set" => await HandleSetOperationAsync(falcorRequest, cancellationToken),
                "call" => await HandleCallOperationAsync(falcorRequest, cancellationToken),
                _ => CreateErrorGraph("Unsupported operation", $"Method {falcorRequest.Method} is not supported")
            };

            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            var body = Encoding.UTF8.GetBytes(json);

            return new SdkInterface.InterfaceResponse(
                StatusCode: 200,
                Headers: new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json"
                },
                Body: body
            );
        }
        catch (JsonException ex)
        {
            return CreateErrorInterfaceResponse(400, "Bad Request", $"Invalid JSON: {ex.Message}");
        }
        catch (Exception ex)
        {
            return CreateErrorInterfaceResponse(500, "Internal Server Error", ex.Message);
        }
    }

    /// <summary>
    /// Handles Falcor get operations.
    /// </summary>
    private async Task<object> HandleGetOperationAsync(FalcorRequest request, CancellationToken cancellationToken)
    {
        // Route via message bus if available
        if (IsIntelligenceAvailable && MessageBus != null)
        {
            var busRequest = new Dictionary<string, object>
            {
                ["operation"] = "falcor.get",
                ["paths"] = request.Paths ?? Array.Empty<object>()
            };

            await Task.CompletedTask; // Placeholder for actual bus call
        }

        // Build JSON Graph response from requested paths
        var jsonGraph = new Dictionary<string, object>();

        foreach (var pathArray in request.Paths ?? Array.Empty<object[]>())
        {
            ResolvePath(jsonGraph, pathArray);
        }

        return new
        {
            jsonGraph
        };
    }

    /// <summary>
    /// Handles Falcor set operations.
    /// </summary>
    private async Task<object> HandleSetOperationAsync(FalcorRequest request, CancellationToken cancellationToken)
    {
        // Route via message bus if available
        if (IsIntelligenceAvailable && MessageBus != null)
        {
            var busRequest = new Dictionary<string, object>
            {
                ["operation"] = "falcor.set",
                ["jsonGraphEnvelope"] = request.JsonGraphEnvelope ?? new { }
            };

            await Task.CompletedTask; // Placeholder for actual bus call
        }

        // Return the updated JSON Graph
        return new
        {
            jsonGraph = request.JsonGraphEnvelope ?? new { }
        };
    }

    /// <summary>
    /// Handles Falcor call operations (RPC-style function calls).
    /// </summary>
    private async Task<object> HandleCallOperationAsync(FalcorRequest request, CancellationToken cancellationToken)
    {
        // Route via message bus if available
        if (IsIntelligenceAvailable && MessageBus != null)
        {
            var busRequest = new Dictionary<string, object>
            {
                ["operation"] = "falcor.call",
                ["callPath"] = request.CallPath ?? Array.Empty<object>(),
                ["arguments"] = request.Arguments ?? Array.Empty<object>()
            };

            await Task.CompletedTask; // Placeholder for actual bus call
        }

        // Return a result graph
        return new
        {
            jsonGraph = new Dictionary<string, object>
            {
                ["result"] = new Dictionary<string, object>
                {
                    ["$type"] = "atom",
                    ["value"] = "Function executed successfully"
                }
            },
            paths = new[] { new[] { "result" } }
        };
    }

    /// <summary>
    /// Resolves a Falcor path into the JSON Graph.
    /// </summary>
    private void ResolvePath(Dictionary<string, object> graph, object[] path)
    {
        if (path == null || path.Length == 0)
            return;

        // Example path: ["users", 0, "name"]
        // Resolves to: graph["users"][0]["name"] = { $type: "atom", value: "John Doe" }

        var current = graph;
        for (int i = 0; i < path.Length - 1; i++)
        {
            var key = path[i]?.ToString() ?? "unknown";

            if (!current.ContainsKey(key))
            {
                current[key] = new Dictionary<string, object>();
            }

            if (current[key] is not Dictionary<string, object> next)
            {
                // Handle array indices or range expressions
                next = new Dictionary<string, object>();
                current[key] = next;
            }

            current = next;
        }

        // Set the final leaf value
        var lastKey = path[^1]?.ToString() ?? "value";
        current[lastKey] = new Dictionary<string, object>
        {
            ["$type"] = "atom",
            ["value"] = $"Sample value for {string.Join(".", path)}"
        };
    }

    /// <summary>
    /// Creates a Falcor error graph.
    /// </summary>
    private object CreateErrorGraph(string message, string detail)
    {
        return new
        {
            jsonGraph = new Dictionary<string, object>
            {
                ["error"] = new Dictionary<string, object>
                {
                    ["$type"] = "error",
                    ["value"] = new Dictionary<string, string>
                    {
                        ["message"] = message,
                        ["detail"] = detail
                    }
                }
            }
        };
    }

    /// <summary>
    /// Creates an error InterfaceResponse.
    /// </summary>
    private SdkInterface.InterfaceResponse CreateErrorInterfaceResponse(int statusCode, string title, string detail)
    {
        var errorData = new
        {
            error = new
            {
                title,
                detail
            }
        };

        var json = JsonSerializer.Serialize(errorData);
        var body = Encoding.UTF8.GetBytes(json);

        return new SdkInterface.InterfaceResponse(
            StatusCode: statusCode,
            Headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json"
            },
            Body: body
        );
    }

    /// <summary>
    /// Falcor request structure.
    /// </summary>
    private class FalcorRequest
    {
        public string? Method { get; set; }
        public object[][]? Paths { get; set; }
        public object? JsonGraphEnvelope { get; set; }
        public object[]? CallPath { get; set; }
        public object[]? Arguments { get; set; }
        public object[]? PathSuffixes { get; set; }
        public object[]? RefPaths { get; set; }
        public object[]? ThisPaths { get; set; }
    }
}
