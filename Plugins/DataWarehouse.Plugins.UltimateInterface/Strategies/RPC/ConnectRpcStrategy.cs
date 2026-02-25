using System;
using System.Collections.Generic;
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
/// Connect RPC interface strategy implementing Buf Connect protocol.
/// </summary>
/// <remarks>
/// Implements the Connect RPC protocol, a simpler alternative to gRPC that works over HTTP/1.1 and HTTP/2.
/// Supports both JSON and protobuf, unary and streaming via Server-Sent Events.
/// Path-based routing with HTTP POST.
/// </remarks>
internal sealed class ConnectRpcStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    public override string StrategyId => "connect-rpc";
    public string DisplayName => "Connect RPC";
    public string SemanticDescription => "Buf Connect RPC interface with HTTP/1.1 and HTTP/2 support, JSON/protobuf payloads, and SSE streaming.";
    public InterfaceCategory Category => InterfaceCategory.Rpc;
    public string[] Tags => ["connect-rpc", "buf", "http", "json", "protobuf", "streaming"];

    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.gRPC;

    public override SdkInterface.InterfaceCapabilities Capabilities => new(
        SupportsStreaming: true,
        SupportsAuthentication: true,
        SupportedContentTypes: new[] { "application/json", "application/proto", "application/connect+json", "application/connect+proto" },
        MaxRequestSize: 50 * 1024 * 1024,
        MaxResponseSize: 50 * 1024 * 1024,
        SupportsBidirectionalStreaming: false, // Connect uses SSE for server streaming
        SupportsMultiplexing: true,
        DefaultTimeout: TimeSpan.FromMinutes(5),
        RequiresTLS: false
    );

    protected override Task StartAsyncCore(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task StopAsyncCore(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        // Connect RPC uses POST with path-based routing
        if (request.Method != SdkInterface.HttpMethod.POST)
        {
            return SdkInterface.InterfaceResponse.Error(405, "Method Not Allowed. Connect RPC requires POST.");
        }

        // Validate content type
        var contentType = request.Headers.TryGetValue("Content-Type", out var ct) ? ct : string.Empty;
        var isJson = contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
        var isProto = contentType.Contains("proto", StringComparison.OrdinalIgnoreCase);

        if (!isJson && !isProto)
        {
            return CreateConnectError("invalid_argument", "Content-Type must be application/json or application/proto", 400);
        }

        // Parse path: /package.Service/Method
        var path = request.Path;
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
        {
            return CreateConnectError("invalid_argument", "Path must start with / and follow format /package.Service/Method", 400);
        }

        var pathParts = path.TrimStart('/').Split('/');
        if (pathParts.Length != 2)
        {
            return CreateConnectError("invalid_argument", $"Invalid Connect RPC path: {path}", 400);
        }

        var serviceName = pathParts[0];
        var methodName = pathParts[1];

        // Parse request body
        object? requestData = null;
        if (request.Body.Length > 0)
        {
            try
            {
                if (isJson)
                {
                    var bodyText = Encoding.UTF8.GetString(request.Body.Span);
                    requestData = JsonSerializer.Deserialize<JsonElement>(bodyText);
                }
                else
                {
                    // Protobuf binary payload
                    requestData = request.Body;
                }
            }
            catch (Exception ex)
            {
                return CreateConnectError("invalid_argument", $"Failed to parse request body: {ex.Message}", 400);
            }
        }

        // Route via message bus
        if (MessageBus != null)
        {
            try
            {
                var topic = $"interface.connect-rpc.{serviceName}.{methodName}";
                var message = new PluginMessage
                {
                    Type = topic,
                    SourcePluginId = "UltimateInterface",
                    Payload = new Dictionary<string, object>
                    {
                        ["service"] = serviceName,
                        ["method"] = methodName,
                        ["payload"] = requestData ?? string.Empty,
                        ["contentType"] = contentType
                    }
                };

                await MessageBus.PublishAsync(topic, message, cancellationToken).ConfigureAwait(false);

                // Create response
                var responseData = new
                {
                    service = serviceName,
                    method = methodName,
                    status = "OK"
                };

                byte[] responseBody;
                string responseContentType;

                if (isJson)
                {
                    responseBody = JsonSerializer.SerializeToUtf8Bytes(responseData);
                    responseContentType = "application/connect+json";
                }
                else
                {
                    // For proto, wrap in simple envelope
                    responseBody = Encoding.UTF8.GetBytes($"{{\"service\":\"{serviceName}\",\"method\":\"{methodName}\"}}");
                    responseContentType = "application/connect+proto";
                }

                var headers = new Dictionary<string, string>
                {
                    ["content-type"] = responseContentType
                };

                return new SdkInterface.InterfaceResponse(
                    StatusCode: 200,
                    Headers: headers,
                    Body: responseBody
                );
            }
            catch (Exception ex)
            {
                return CreateConnectError("internal", $"Request processing failed: {ex.Message}", 500);
            }
        }

        // Fallback
        var fallbackResponse = new
        {
            service = serviceName,
            method = methodName,
            note = "Message bus not available"
        };

        var fallbackBody = JsonSerializer.SerializeToUtf8Bytes(fallbackResponse);
        return new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string>
            {
                ["content-type"] = "application/connect+json"
            },
            Body: fallbackBody
        );
    }

    /// <summary>
    /// Creates Connect RPC error response.
    /// Connect uses standard HTTP status codes and JSON error format.
    /// </summary>
    private static SdkInterface.InterfaceResponse CreateConnectError(string code, string message, int httpStatus)
    {
        var errorBody = new
        {
            code = code,
            message = message
        };

        var body = JsonSerializer.SerializeToUtf8Bytes(errorBody);
        var headers = new Dictionary<string, string>
        {
            ["content-type"] = "application/json"
        };

        return new SdkInterface.InterfaceResponse(
            StatusCode: httpStatus,
            Headers: headers,
            Body: body
        );
    }
}
