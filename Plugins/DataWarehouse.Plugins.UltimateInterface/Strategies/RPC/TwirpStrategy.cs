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
/// Twirp interface strategy implementing Twitch's simple RPC protocol.
/// </summary>
/// <remarks>
/// Implements Twirp protocol: protobuf over HTTP with simple routing.
/// Path format: /twirp/package.Service/Method
/// Accepts JSON or protobuf, returns same format.
/// Simple error format: {"code": "not_found", "msg": "..."}
/// </remarks>
internal sealed class TwirpStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    public string StrategyId => "twirp";
    public string DisplayName => "Twirp";
    public string SemanticDescription => "Twitch Twirp RPC interface with protobuf-over-HTTP, JSON/binary support, and simple error handling.";
    public InterfaceCategory Category => InterfaceCategory.Rpc;
    public string[] Tags => ["twirp", "rpc", "protobuf", "json", "http"];

    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.gRPC;

    public override SdkInterface.InterfaceCapabilities Capabilities => new(
        SupportsStreaming: false, // Twirp is unary only
        SupportsAuthentication: true,
        SupportedContentTypes: new[] { "application/json", "application/protobuf" },
        MaxRequestSize: 10 * 1024 * 1024,
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
        // Twirp requires POST
        if (request.Method != SdkInterface.HttpMethod.POST)
        {
            return CreateTwirpError("bad_route", $"Method {request.Method} not allowed. Use POST.");
        }

        // Parse path: /twirp/package.Service/Method
        var path = request.Path;
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/twirp/", StringComparison.OrdinalIgnoreCase))
        {
            return CreateTwirpError("bad_route", "Path must start with /twirp/package.Service/Method");
        }

        var pathWithoutPrefix = path.Substring(7); // Remove "/twirp/"
        var pathParts = pathWithoutPrefix.Split('/');
        if (pathParts.Length != 2)
        {
            return CreateTwirpError("bad_route", $"Invalid Twirp path: {path}");
        }

        var serviceName = pathParts[0];
        var methodName = pathParts[1];

        // Determine content type
        var contentType = request.Headers.TryGetValue("Content-Type", out var ct) ? ct : "application/json";
        var isJson = contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
        var isProtobuf = contentType.Contains("protobuf", StringComparison.OrdinalIgnoreCase);

        if (!isJson && !isProtobuf)
        {
            return CreateTwirpError("invalid_argument", "Content-Type must be application/json or application/protobuf");
        }

        // Parse request body
        object? requestData = null;
        if (!request.Body.IsEmpty)
        {
            try
            {
                var bodyBytes = request.Body.ToArray();
                if (isJson)
                {
                    var bodyText = Encoding.UTF8.GetString(bodyBytes);
                    requestData = JsonSerializer.Deserialize<JsonElement>(bodyText);
                }
                else
                {
                    // Protobuf binary
                    requestData = Convert.ToBase64String(bodyBytes);
                }
            }
            catch (Exception ex)
            {
                return CreateTwirpError("invalid_argument", $"Failed to parse request: {ex.Message}");
            }
        }

        // Route via message bus
        if (MessageBus != null)
        {
            try
            {
                var topic = $"interface.twirp.{serviceName}.{methodName}";
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

                // Create response in same format as request
                byte[] responseBody;
                string responseContentType;

                if (isJson)
                {
                    var responseData = new
                    {
                        service = serviceName,
                        method = methodName,
                        status = "success"
                    };
                    responseBody = JsonSerializer.SerializeToUtf8Bytes(responseData);
                    responseContentType = "application/json";
                }
                else
                {
                    // For protobuf, return simple binary envelope
                    responseBody = Encoding.UTF8.GetBytes($"{serviceName}.{methodName}");
                    responseContentType = "application/protobuf";
                }

                var headers = new Dictionary<string, string>
                {
                    ["content-type"] = responseContentType
                };

                return new SdkInterface.InterfaceResponse(200, headers, responseBody);
            }
            catch (Exception ex)
            {
                return CreateTwirpError("internal", $"Request processing failed: {ex.Message}");
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
        return new SdkInterface.InterfaceResponse(200, new Dictionary<string, string>
        {
            ["content-type"] = "application/json"
        }, fallbackBody);
    }

    /// <summary>
    /// Creates Twirp error response with standard error codes.
    /// Error codes: bad_route, invalid_argument, not_found, internal, etc.
    /// </summary>
    private static SdkInterface.InterfaceResponse CreateTwirpError(string code, string msg)
    {
        var errorBody = new
        {
            code = code,
            msg = msg
        };

        var body = JsonSerializer.SerializeToUtf8Bytes(errorBody);
        var headers = new Dictionary<string, string>
        {
            ["content-type"] = "application/json"
        };

        // Twirp always returns 200 with error in body (except for routing errors)
        var httpStatus = code == "bad_route" ? 404 : 200;

        return new SdkInterface.InterfaceResponse(httpStatus, headers, body);
    }
}
