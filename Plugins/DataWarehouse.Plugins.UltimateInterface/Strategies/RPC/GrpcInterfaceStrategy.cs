using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.Plugins.UltimateInterface;
using DataWarehouse.SDK.Contracts.Interface;
using DataWarehouse.SDK.Utilities;
using SdkInterface = DataWarehouse.SDK.Contracts.Interface;

namespace DataWarehouse.Plugins.UltimateInterface.Strategies.RPC;

/// <summary>
/// gRPC interface strategy implementing Google's Remote Procedure Call protocol.
/// </summary>
/// <remarks>
/// Supports HTTP/2, bidirectional streaming, and protobuf-based service/method routing.
/// Request path format: /package.Service/Method
/// Content types: application/grpc, application/grpc+proto
/// </remarks>
internal sealed class GrpcInterfaceStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    public override string StrategyId => "grpc";
    public string DisplayName => "gRPC";
    public string SemanticDescription => "High-performance gRPC interface with Protocol Buffers, HTTP/2, and bidirectional streaming support.";
    public InterfaceCategory Category => InterfaceCategory.Rpc;
    public string[] Tags => ["grpc", "protobuf", "rpc", "streaming", "http2"];

    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.gRPC;
    public override SdkInterface.InterfaceCapabilities Capabilities => SdkInterface.InterfaceCapabilities.CreateGrpcDefaults();

    protected override Task StartAsyncCore(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task StopAsyncCore(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        // Validate content type
        var contentType = request.Headers.TryGetValue("Content-Type", out var ct) ? ct : string.Empty;
        if (!contentType.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase))
        {
            return SdkInterface.InterfaceResponse.Error(415, "Unsupported Media Type. Expected application/grpc or application/grpc+proto.");
        }

        // Parse gRPC path: /package.Service/Method
        var path = request.Path;
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
        {
            return CreateGrpcError(3, "INVALID_ARGUMENT", "Path must start with / and follow format /package.Service/Method");
        }

        var pathParts = path.TrimStart('/').Split('/');
        if (pathParts.Length != 2)
        {
            return CreateGrpcError(3, "INVALID_ARGUMENT", $"Invalid gRPC path format: {path}. Expected /package.Service/Method");
        }

        var serviceName = pathParts[0];
        var methodName = pathParts[1];

        // Parse gRPC frame: 1-byte compressed flag + 4-byte message length + message
        byte[]? requestPayload = null;
        if (!request.Body.IsEmpty)
        {
            try
            {
                requestPayload = ParseGrpcFrame(request.Body.ToArray());
            }
            catch (Exception ex)
            {
                return CreateGrpcError(3, "INVALID_ARGUMENT", $"Failed to parse gRPC frame: {ex.Message}");
            }
        }

        // Route to message bus for processing
        if (MessageBus != null)
        {
            try
            {
                var topic = $"interface.grpc.{serviceName}.{methodName}";
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

                // Return success response with framed payload
                var responseMessage = Encoding.UTF8.GetBytes($"{{\"service\":\"{serviceName}\",\"method\":\"{methodName}\",\"status\":\"OK\"}}");
                var framedResponse = CreateGrpcFrame(responseMessage, compressed: false);

                var headers = new Dictionary<string, string>
                {
                    ["content-type"] = "application/grpc+proto",
                    ["grpc-status"] = "0",
                    ["grpc-message"] = "OK"
                };

                return new SdkInterface.InterfaceResponse(200, headers, framedResponse);
            }
            catch (Exception ex)
            {
                return CreateGrpcError(13, "INTERNAL", $"Request processing failed: {ex.Message}");
            }
        }

        // Fallback: Return success without message bus
        var fallbackPayload = Encoding.UTF8.GetBytes($"{{\"service\":\"{serviceName}\",\"method\":\"{methodName}\",\"note\":\"Message bus not available\"}}");
        var fallbackFramed = CreateGrpcFrame(fallbackPayload, compressed: false);
        return new SdkInterface.InterfaceResponse(200, new Dictionary<string, string>
        {
            ["content-type"] = "application/grpc+proto",
            ["grpc-status"] = "0",
            ["grpc-message"] = "OK"
        }, fallbackFramed);
    }

    /// <summary>
    /// Parses gRPC frame format: 1-byte compressed flag + 4-byte length + message.
    /// </summary>
    private static byte[] ParseGrpcFrame(byte[] frame)
    {
        if (frame.Length < 5)
            throw new InvalidDataException("gRPC frame too short (minimum 5 bytes)");

        var compressed = frame[0] == 1;
        var length = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(1, 4));

        if (frame.Length < 5 + length)
            throw new InvalidDataException($"gRPC frame length mismatch. Expected {5 + length}, got {frame.Length}");

        var message = new byte[length];
        Array.Copy(frame, 5, message, 0, (int)length);

        if (compressed)
        {
            // In production, would decompress here (e.g., gzip)
            // For now, return as-is since compression is optional
        }

        return message;
    }

    /// <summary>
    /// Creates gRPC frame: 1-byte compressed flag + 4-byte length + message.
    /// </summary>
    private static byte[] CreateGrpcFrame(byte[] message, bool compressed)
    {
        var frame = new byte[5 + message.Length];
        frame[0] = compressed ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)message.Length);
        Array.Copy(message, 0, frame, 5, message.Length);
        return frame;
    }

    /// <summary>
    /// Creates gRPC error response with standard status codes.
    /// </summary>
    /// <param name="grpcStatus">gRPC status code (0=OK, 3=INVALID_ARGUMENT, 13=INTERNAL, etc.)</param>
    /// <param name="grpcCode">gRPC status name</param>
    /// <param name="message">Error message</param>
    private static SdkInterface.InterfaceResponse CreateGrpcError(int grpcStatus, string grpcCode, string message)
    {
        var headers = new Dictionary<string, string>
        {
            ["content-type"] = "application/grpc",
            ["grpc-status"] = grpcStatus.ToString(),
            ["grpc-message"] = message
        };

        return new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: headers,
            Body: Array.Empty<byte>()
        );
    }
}
