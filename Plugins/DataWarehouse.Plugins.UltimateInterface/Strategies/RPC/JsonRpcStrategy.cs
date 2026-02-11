using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.Plugins.UltimateInterface;
using DataWarehouse.SDK.Contracts.Interface;
using DataWarehouse.SDK.Utilities;
using SdkInterface = DataWarehouse.SDK.Contracts.Interface;

namespace DataWarehouse.Plugins.UltimateInterface.Strategies.RPC;

/// <summary>
/// JSON-RPC 2.0 interface strategy implementing the JSON-RPC specification.
/// </summary>
/// <remarks>
/// Implements JSON-RPC 2.0 specification with:
/// - Standard request format: {"jsonrpc": "2.0", "method": "...", "params": [...], "id": N}
/// - Standard response format: {"jsonrpc": "2.0", "result": ..., "id": N}
/// - Batch requests (array of requests)
/// - Notifications (no id = no response)
/// - Standard error codes: -32700 (parse error), -32600 (invalid request), -32601 (method not found), -32602 (invalid params), -32603 (internal error)
/// </remarks>
internal sealed class JsonRpcStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    public string StrategyId => "json-rpc";
    public string DisplayName => "JSON-RPC 2.0";
    public string SemanticDescription => "JSON-RPC 2.0 interface with batch requests, notifications, and standard error codes.";
    public InterfaceCategory Category => InterfaceCategory.Rpc;
    public string[] Tags => ["json-rpc", "rpc", "json", "batch"];

    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.JsonRpc;

    public override SdkInterface.InterfaceCapabilities Capabilities => new(
        SupportsStreaming: false,
        SupportsAuthentication: true,
        SupportedContentTypes: new[] { "application/json", "application/json-rpc" },
        MaxRequestSize: 5 * 1024 * 1024,
        MaxResponseSize: 10 * 1024 * 1024,
        SupportsBidirectionalStreaming: false,
        SupportsMultiplexing: false,
        DefaultTimeout: TimeSpan.FromSeconds(30),
        RequiresTLS: false
    );

    protected override Task StartAsyncCore(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task StopAsyncCore(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        // Validate content type
        var contentType = request.Headers.TryGetValue("Content-Type", out var ct) ? ct : string.Empty;
        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return CreateJsonRpcError(null, -32700, "Parse error", "Content-Type must be application/json");
        }

        if (request.Body.Length == 0)
        {
            return CreateJsonRpcError(null, -32600, "Invalid Request", "Request body is empty");
        }

        try
        {
            var bodyText = Encoding.UTF8.GetString(request.Body.Span);
            using var doc = JsonDocument.Parse(bodyText);

            // Check if batch request (array)
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return await HandleBatchRequestAsync(doc.RootElement, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return await HandleSingleRequestAsync(doc.RootElement, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (JsonException ex)
        {
            return CreateJsonRpcError(null, -32700, "Parse error", ex.Message);
        }
        catch (Exception ex)
        {
            return CreateJsonRpcError(null, -32603, "Internal error", ex.Message);
        }
    }

    /// <summary>
    /// Handles a single JSON-RPC request.
    /// </summary>
    private async Task<SdkInterface.InterfaceResponse> HandleSingleRequestAsync(JsonElement element, CancellationToken cancellationToken)
    {
        // Validate JSON-RPC 2.0 format
        if (!element.TryGetProperty("jsonrpc", out var versionProp) || versionProp.GetString() != "2.0")
        {
            return CreateJsonRpcError(null, -32600, "Invalid Request", "jsonrpc property must be '2.0'");
        }

        if (!element.TryGetProperty("method", out var methodProp) || methodProp.ValueKind != JsonValueKind.String)
        {
            return CreateJsonRpcError(null, -32600, "Invalid Request", "method property is required and must be a string");
        }

        var method = methodProp.GetString()!;
        var hasId = element.TryGetProperty("id", out var idProp);
        var id = hasId ? idProp : (JsonElement?)null;

        // Extract params (optional)
        object? paramsValue = null;
        if (element.TryGetProperty("params", out var paramsProp))
        {
            paramsValue = paramsProp;
        }

        // Route via message bus
        if (MessageBus != null)
        {
            try
            {
                var topic = $"interface.json-rpc.{method}";
                var message = new PluginMessage
                {
                    Type = topic,
                    SourcePluginId = "UltimateInterface",
                    Payload = new Dictionary<string, object>
                    {
                        ["method"] = method,
                        ["parameters"] = paramsValue?.ToString() ?? string.Empty,
                        ["id"] = id?.ToString() ?? string.Empty
                    }
                };

                await MessageBus.PublishAsync(topic, message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (hasId)
                {
                    return CreateJsonRpcError(id, -32603, "Internal error", ex.Message);
                }
                // Notification - no response
                return new SdkInterface.InterfaceResponse(
                    StatusCode: 200,
                    Headers: new Dictionary<string, string>(),
                    Body: Array.Empty<byte>()
                );
            }
        }

        // If notification (no id), return empty response
        if (!hasId)
        {
            return new SdkInterface.InterfaceResponse(200, new Dictionary<string, string>(), Array.Empty<byte>());
        }

        // Create success response
        var response = new
        {
            jsonrpc = "2.0",
            result = new { method = method, status = "executed" },
            id = id?.ToString()
        };

        var responseBody = JsonSerializer.SerializeToUtf8Bytes(response);
        return new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string>
            {
                ["content-type"] = "application/json"
            },
            Body: responseBody
        );
    }

    /// <summary>
    /// Handles batch JSON-RPC requests.
    /// </summary>
    private async Task<SdkInterface.InterfaceResponse> HandleBatchRequestAsync(JsonElement array, CancellationToken cancellationToken)
    {
        if (array.GetArrayLength() == 0)
        {
            return CreateJsonRpcError(null, -32600, "Invalid Request", "Batch request cannot be empty");
        }

        var responses = new List<object>();

        foreach (var element in array.EnumerateArray())
        {
            var singleResponse = await HandleSingleRequestAsync(element, cancellationToken).ConfigureAwait(false);

            // Only include responses for non-notification requests (those with id)
            if (singleResponse.Body.Length > 0)
            {
                try
                {
                    var responseObj = JsonSerializer.Deserialize<JsonElement>(singleResponse.Body.Span);
                    responses.Add(responseObj);
                }
                catch
                {
                    // Skip malformed responses
                }
            }
        }

        // If all requests were notifications, return empty
        if (responses.Count == 0)
        {
            return new SdkInterface.InterfaceResponse(
                StatusCode: 200,
                Headers: new Dictionary<string, string>(),
                Body: Array.Empty<byte>()
            );
        }

        var batchResponseBody = JsonSerializer.SerializeToUtf8Bytes(responses);
        return new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string>
            {
                ["content-type"] = "application/json"
            },
            Body: batchResponseBody
        );
    }

    /// <summary>
    /// Creates JSON-RPC 2.0 error response.
    /// </summary>
    /// <param name="id">Request id (null for parse errors)</param>
    /// <param name="code">Error code (-32700 to -32603 for standard errors)</param>
    /// <param name="message">Error message</param>
    /// <param name="data">Additional error data</param>
    private static SdkInterface.InterfaceResponse CreateJsonRpcError(JsonElement? id, int code, string message, string? data = null)
    {
        var error = new
        {
            code = code,
            message = message,
            data = data
        };

        var response = new
        {
            jsonrpc = "2.0",
            error = error,
            id = id?.ToString()
        };

        var body = JsonSerializer.SerializeToUtf8Bytes(response);
        return new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string>
            {
                ["content-type"] = "application/json"
            },
            Body: body
        );
    }
}
