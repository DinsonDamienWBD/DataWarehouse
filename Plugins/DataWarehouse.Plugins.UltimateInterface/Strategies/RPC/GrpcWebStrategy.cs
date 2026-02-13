using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.Plugins.UltimateInterface;
using DataWarehouse.SDK.Contracts.Interface;
using DataWarehouse.SDK.Utilities;
using SdkInterface = DataWarehouse.SDK.Contracts.Interface;

namespace DataWarehouse.Plugins.UltimateInterface.Strategies.RPC;

/// <summary>
/// gRPC-Web interface strategy adapting gRPC for browser clients.
/// </summary>
/// <remarks>
/// Adapts gRPC for browsers that cannot use HTTP/2 directly.
/// Supports HTTP/1.1, base64-encoded frames, and CORS headers for cross-origin requests.
/// Content type: application/grpc-web, application/grpc-web+proto
/// </remarks>
internal sealed class GrpcWebStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    public override string StrategyId => "grpc-web";
    public string DisplayName => "gRPC-Web";
    public string SemanticDescription => "Browser-compatible gRPC-Web interface with HTTP/1.1 support, base64 encoding, and CORS.";
    public InterfaceCategory Category => InterfaceCategory.Rpc;
    public string[] Tags => ["grpc-web", "browser", "http", "cors", "base64"];

    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.gRPC;

    public override SdkInterface.InterfaceCapabilities Capabilities => new(
        SupportsStreaming: true,
        SupportsAuthentication: true,
        SupportedContentTypes: new[] { "application/grpc-web", "application/grpc-web+proto", "application/grpc-web-text" },
        MaxRequestSize: 50 * 1024 * 1024, // 50 MB
        MaxResponseSize: 50 * 1024 * 1024,
        SupportsBidirectionalStreaming: false, // gRPC-Web only supports server streaming
        SupportsMultiplexing: false,
        DefaultTimeout: TimeSpan.FromMinutes(5),
        RequiresTLS: false // Can work over HTTP/1.1
    );

    protected override Task StartAsyncCore(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task StopAsyncCore(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        // Validate content type
        var contentType = request.Headers.TryGetValue("Content-Type", out var ct) ? ct : string.Empty;
        if (!contentType.StartsWith("application/grpc-web", StringComparison.OrdinalIgnoreCase))
        {
            return SdkInterface.InterfaceResponse.Error(415, "Unsupported Media Type. Expected application/grpc-web.");
        }

        var isBase64 = contentType.Contains("text") || contentType.Contains("base64");

        // Parse path: /package.Service/Method
        var path = request.Path;
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
        {
            return CreateGrpcWebError(3, "Invalid path format");
        }

        var pathParts = path.TrimStart('/').Split('/');
        if (pathParts.Length != 2)
        {
            return CreateGrpcWebError(3, $"Invalid gRPC-Web path: {path}");
        }

        var serviceName = pathParts[0];
        var methodName = pathParts[1];

        // Decode request body
        byte[]? requestPayload = null;
        if (request.Body.Length > 0)
        {
            try
            {
                var bodyData = isBase64 ? Convert.FromBase64String(Encoding.UTF8.GetString(request.Body.Span)) : request.Body.ToArray();

                // gRPC-Web uses same frame format as gRPC
                if (bodyData.Length >= 5)
                {
                    var length = BinaryPrimitives.ReadUInt32BigEndian(bodyData.AsSpan(1, 4));
                    requestPayload = new byte[length];
                    Array.Copy(bodyData, 5, requestPayload, 0, (int)length);
                }
            }
            catch (Exception ex)
            {
                return CreateGrpcWebError(3, $"Failed to decode gRPC-Web frame: {ex.Message}");
            }
        }

        // Route via message bus
        if (MessageBus != null)
        {
            try
            {
                var topic = $"interface.grpc-web.{serviceName}.{methodName}";
                var message = new PluginMessage
                {
                    Type = topic,
                    SourcePluginId = "UltimateInterface",
                    Payload = new Dictionary<string, object>
                    {
                        ["service"] = serviceName,
                        ["method"] = methodName,
                        ["payload"] = requestPayload != null ? Convert.ToBase64String(requestPayload) : string.Empty
                    }
                };

                await MessageBus.PublishAsync(topic, message, cancellationToken).ConfigureAwait(false);

                // Create response
                var responseMessage = Encoding.UTF8.GetBytes($"{{\"service\":\"{serviceName}\",\"method\":\"{methodName}\",\"status\":\"OK\"}}");
                var framedResponse = CreateGrpcWebFrame(responseMessage);

                // Base64-encode if requested
                var finalBody = isBase64 ? Encoding.UTF8.GetBytes(Convert.ToBase64String(framedResponse)) : framedResponse;

                var headers = new Dictionary<string, string>
                {
                    ["content-type"] = isBase64 ? "application/grpc-web-text+proto" : "application/grpc-web+proto",
                    ["grpc-status"] = "0",
                    ["grpc-message"] = "OK",
                    ["access-control-allow-origin"] = "*", // CORS support
                    ["access-control-allow-methods"] = "POST, GET, OPTIONS",
                    ["access-control-allow-headers"] = "content-type, x-grpc-web, x-user-agent"
                };

                return new SdkInterface.InterfaceResponse(
                    StatusCode: 200,
                    Headers: headers,
                    Body: finalBody
                );
            }
            catch (Exception ex)
            {
                return CreateGrpcWebError(13, $"Request processing failed: {ex.Message}");
            }
        }

        // Fallback
        var fallbackPayload = Encoding.UTF8.GetBytes($"{{\"service\":\"{serviceName}\",\"method\":\"{methodName}\"}}");
        var fallbackFramed = CreateGrpcWebFrame(fallbackPayload);
        var fallbackBody = isBase64 ? Encoding.UTF8.GetBytes(Convert.ToBase64String(fallbackFramed)) : fallbackFramed;

        return new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string>
            {
                ["content-type"] = isBase64 ? "application/grpc-web-text+proto" : "application/grpc-web+proto",
                ["grpc-status"] = "0",
                ["access-control-allow-origin"] = "*"
            },
            Body: fallbackBody
        );
    }

    /// <summary>
    /// Creates gRPC-Web frame (same as gRPC: 1-byte compressed + 4-byte length + message).
    /// </summary>
    private static byte[] CreateGrpcWebFrame(byte[] message)
    {
        var frame = new byte[5 + message.Length];
        frame[0] = 0; // Not compressed
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)message.Length);
        Array.Copy(message, 0, frame, 5, message.Length);
        return frame;
    }

    /// <summary>
    /// Creates gRPC-Web error response.
    /// </summary>
    private static SdkInterface.InterfaceResponse CreateGrpcWebError(int grpcStatus, string message)
    {
        var headers = new Dictionary<string, string>
        {
            ["content-type"] = "application/grpc-web",
            ["grpc-status"] = grpcStatus.ToString(),
            ["grpc-message"] = message,
            ["access-control-allow-origin"] = "*"
        };

        return new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: headers,
            Body: Array.Empty<byte>()
        );
    }
}
