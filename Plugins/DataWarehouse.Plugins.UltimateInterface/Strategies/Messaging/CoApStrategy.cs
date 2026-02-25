using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Edge.Protocols;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SdkInterface = DataWarehouse.SDK.Contracts.Interface;

namespace DataWarehouse.Plugins.UltimateInterface.Strategies.Messaging;

/// <summary>
/// CoAP (Constrained Application Protocol) interface strategy for IoT and edge computing.
/// </summary>
/// <remarks>
/// <para>
/// CoAP is a lightweight RESTful protocol designed for constrained devices and networks.
/// Runs over UDP with GET/POST/PUT/DELETE methods similar to HTTP REST APIs.
/// </para>
/// <para>
/// <strong>Features</strong>:
/// <list type="bullet">
/// <item><description>Resource discovery via /.well-known/core</description></item>
/// <item><description>Confirmable and non-confirmable messages</description></item>
/// <item><description>Observable resources for push notifications</description></item>
/// <item><description>Block-wise transfer for large payloads</description></item>
/// <item><description>DTLS 1.2 for secure communication</description></item>
/// </list>
/// </para>
/// <para>
/// This strategy exposes DataWarehouse resources via CoAP protocol, enabling access from
/// edge devices, IoT sensors, and constrained environments where HTTP overhead is prohibitive.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: CoAP resource strategy (EDGE-03)")]
internal sealed class CoApStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    private ICoApClient? _coapClient;
    private readonly string _serverUri;
    private readonly ConcurrentDictionary<string, Func<byte[], Task<byte[]>>> _resourceHandlers = new();

    public override string StrategyId => "coap";
    public string DisplayName => "CoAP";
    public string SemanticDescription => "CoAP (Constrained Application Protocol) for IoT and edge device communication with resource discovery and observability.";
    public InterfaceCategory Category => InterfaceCategory.Messaging;
    public string[] Tags => ["coap", "iot", "edge", "rest", "udp"];

    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.Custom; // Or add CoAP to enum

    public override SdkInterface.InterfaceCapabilities Capabilities => new(
        SupportsStreaming: false, // CoAP uses request-response
        SupportsAuthentication: false, // Phase 36: DTLS not implemented
        SupportedContentTypes: new[] { "application/json", "application/octet-stream", "text/plain", "application/link-format" },
        MaxRequestSize: 1024, // CoAP typical max without block-wise transfer
        MaxResponseSize: 1024,
        SupportsBidirectionalStreaming: false,
        SupportsMultiplexing: true, // Multiple concurrent requests over UDP
        DefaultTimeout: TimeSpan.FromSeconds(5),
        SupportsCancellation: true,
        RequiresTLS: false // DTLS optional
    );

    /// <summary>
    /// Initializes a new instance of the <see cref="CoApStrategy"/> class.
    /// </summary>
    /// <param name="serverUri">CoAP server URI (default: coap://localhost:5683).</param>
    public CoApStrategy(string serverUri = "coap://localhost:5683")
    {
        _serverUri = serverUri;
    }

    protected override Task StartAsyncCore(CancellationToken cancellationToken)
    {
        _coapClient = new CoApClient();
        return Task.CompletedTask;
    }

    protected override async Task StopAsyncCore(CancellationToken cancellationToken)
    {
        if (_coapClient is not null)
        {
            await _coapClient.DisposeAsync();
            _coapClient = null;
        }
        _resourceHandlers.Clear();
    }

    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        if (_coapClient is null)
            return CreateErrorResponse("CoAP client not initialized");

        try
        {
            // Parse CoAP method from HTTP method or custom header
            var method = request.Method switch
            {
                SdkInterface.HttpMethod.GET => CoApMethod.GET,
                SdkInterface.HttpMethod.POST => CoApMethod.POST,
                SdkInterface.HttpMethod.PUT => CoApMethod.PUT,
                SdkInterface.HttpMethod.DELETE => CoApMethod.DELETE,
                _ => CoApMethod.GET
            };

            // Build CoAP request
            var uri = $"{_serverUri}{request.Path}";
            var payload = request.Body.IsEmpty ? Array.Empty<byte>() : request.Body.ToArray();

            var coapRequest = new CoApRequest
            {
                Method = method,
                Uri = uri,
                Payload = payload,
                Type = CoApMessageType.Confirmable
            };

            // Send CoAP request
            var coapResponse = await _coapClient.SendAsync(coapRequest, cancellationToken);

            // Convert CoAP response to InterfaceResponse
            var statusCode = coapResponse.Code switch
            {
                CoApResponseCode.Content => 200,
                CoApResponseCode.Created => 201,
                CoApResponseCode.Deleted => 204,
                CoApResponseCode.Changed => 204,
                CoApResponseCode.BadRequest => 400,
                CoApResponseCode.Unauthorized => 401,
                CoApResponseCode.Forbidden => 403,
                CoApResponseCode.NotFound => 404,
                CoApResponseCode.InternalServerError => 500,
                _ => 500
            };

            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/octet-stream",
                ["X-CoAP-Code"] = ((int)coapResponse.Code).ToString()
            };

            return new SdkInterface.InterfaceResponse(
                statusCode,
                headers,
                coapResponse.Payload,
                null);
        }
        catch (TimeoutException)
        {
            return CreateErrorResponse("CoAP request timed out", 504);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse($"CoAP request failed: {ex.Message}", 500);
        }
    }

    /// <summary>
    /// Registers a local CoAP resource handler (not used in client mode).
    /// </summary>
    public Task RegisterResourceAsync(string path, Func<byte[], Task<byte[]>> handler, CancellationToken ct = default)
    {
        _resourceHandlers[path] = handler;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Discovers resources via /.well-known/core.
    /// </summary>
    public async Task<IReadOnlyList<string>> DiscoverResourcesAsync(CancellationToken ct = default)
    {
        if (_coapClient is null)
            return Array.Empty<string>();

        var resources = await _coapClient.DiscoverAsync(_serverUri, ct);
        return resources.Select(r => r.Path).ToList();
    }

    /// <summary>
    /// Creates an error response.
    /// </summary>
    private SdkInterface.InterfaceResponse CreateErrorResponse(string message, int statusCode = 500)
    {
        var errorJson = JsonSerializer.Serialize(new { error = message });
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json"
        };
        return new SdkInterface.InterfaceResponse(
            statusCode,
            headers,
            Encoding.UTF8.GetBytes(errorJson),
            null);
    }
}
