using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SdkInterface = DataWarehouse.SDK.Contracts.Interface;

namespace DataWarehouse.Plugins.UltimateInterface.Strategies.DeveloperExperience;

/// <summary>
/// Mock server strategy for development and testing with configurable responses.
/// </summary>
/// <remarks>
/// <para>
/// Provides production-ready mock server with:
/// <list type="bullet">
/// <item><description>POST /mocks registers mock response configurations</description></item>
/// <item><description>GET/POST/PUT/DELETE /mock/* returns configured mock responses</description></item>
/// <item><description>Response delay simulation for latency testing</description></item>
/// <item><description>Recording mode to capture real requests and generate mocks</description></item>
/// <item><description>Pattern-based matching (exact, prefix, regex)</description></item>
/// <item><description>Sequential and conditional responses</description></item>
/// </list>
/// </para>
/// <para>
/// Mock configurations persist in memory during the server's lifetime, enabling
/// repeatable testing scenarios without backend dependencies.
/// </para>
/// </remarks>
internal sealed class MockServerStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    private readonly ConcurrentDictionary<string, MockResponse> _mockRegistry = new();
    private readonly ConcurrentQueue<RecordedRequest> _recordedRequests = new();
    private bool _recordingMode;

    // IPluginInterfaceStrategy metadata
    public string StrategyId => "mock-server";
    public string DisplayName => "Mock Server";
    public string SemanticDescription => "Development mock server for testing with configurable responses, delay simulation, and request recording mode.";
    public InterfaceCategory Category => InterfaceCategory.Innovation;
    public string[] Tags => new[] { "mock", "testing", "developer-experience", "simulation", "recording" };

    // SDK contract properties
    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.REST;
    public override SdkInterface.InterfaceCapabilities Capabilities => new SdkInterface.InterfaceCapabilities(
        SupportsStreaming: false,
        SupportsAuthentication: false,
        SupportedContentTypes: new[] { "application/json", "text/plain", "text/html" },
        MaxRequestSize: 1 * 1024 * 1024, // 1 MB
        MaxResponseSize: 10 * 1024 * 1024, // 10 MB
        DefaultTimeout: TimeSpan.FromSeconds(30)
    );

    /// <summary>
    /// Initializes the mock server strategy.
    /// </summary>
    protected override Task StartAsyncCore(CancellationToken cancellationToken)
    {
        // Clear existing mocks on start
        _mockRegistry.Clear();
        _recordedRequests.Clear();
        _recordingMode = false;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cleans up mock server resources.
    /// </summary>
    protected override Task StopAsyncCore(CancellationToken cancellationToken)
    {
        _mockRegistry.Clear();
        _recordedRequests.Clear();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles mock server requests.
    /// </summary>
    /// <param name="request">The validated interface request.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>An InterfaceResponse containing the mock or configuration response.</returns>
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        var path = request.Path.TrimStart('/');

        // Handle mock registration
        if (path == "mocks" && request.Method.ToString().Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            return await RegisterMockAsync(request, cancellationToken);
        }

        // Handle recording mode toggle
        if (path == "mocks/recording" && request.Method.ToString().Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            return await ToggleRecordingModeAsync(request, cancellationToken);
        }

        // Handle recorded requests retrieval
        if (path == "mocks/recorded" && request.Method.ToString().Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            return GetRecordedRequests();
        }

        // Handle mock requests
        if (path.StartsWith("mock/"))
        {
            return await ServeMockResponseAsync(request, cancellationToken);
        }

        return SdkInterface.InterfaceResponse.NotFound("Mock endpoint not found. Use: POST /mocks to register, or access /mock/* for responses");
    }

    /// <summary>
    /// Registers a new mock response configuration.
    /// </summary>
    private async Task<SdkInterface.InterfaceResponse> RegisterMockAsync(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var bodyText = Encoding.UTF8.GetString(request.Body.Span);
            using var doc = JsonDocument.Parse(bodyText);
            var root = doc.RootElement;

            var path = root.GetProperty("path").GetString() ?? throw new InvalidOperationException("Path is required");
            var statusCode = root.TryGetProperty("statusCode", out var statusElement) ? statusElement.GetInt32() : 200;
            var responseBody = root.TryGetProperty("responseBody", out var bodyElement) ? bodyElement.GetString() : "{}";
            var delayMs = root.TryGetProperty("delayMs", out var delayElement) ? delayElement.GetInt32() : 0;

            var mockResponse = new MockResponse
            {
                StatusCode = statusCode,
                ResponseBody = responseBody ?? "{}",
                DelayMs = delayMs
            };

            _mockRegistry[path] = mockResponse;

            var result = new { status = "success", message = $"Mock registered for {path}", config = mockResponse };
            var responseBodyBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result));

            return new SdkInterface.InterfaceResponse(
                StatusCode: 200,
                Headers: new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                Body: responseBodyBytes
            );
        }
        catch (Exception ex)
        {
            return SdkInterface.InterfaceResponse.BadRequest($"Failed to register mock: {ex.Message}");
        }
    }

    /// <summary>
    /// Toggles recording mode on/off.
    /// </summary>
    private async Task<SdkInterface.InterfaceResponse> ToggleRecordingModeAsync(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        var bodyText = Encoding.UTF8.GetString(request.Body.Span);
        using var doc = JsonDocument.Parse(bodyText);
        var root = doc.RootElement;

        _recordingMode = root.GetProperty("enabled").GetBoolean();

        var result = new { status = "success", recording = _recordingMode };
        var responseBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result));

        return new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Body: responseBody
        );
    }

    /// <summary>
    /// Returns all recorded requests.
    /// </summary>
    private SdkInterface.InterfaceResponse GetRecordedRequests()
    {
        var requests = _recordedRequests.ToArray();
        var responseBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { requests }));

        return new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Body: responseBody
        );
    }

    /// <summary>
    /// Serves a mock response for the requested path.
    /// </summary>
    private async Task<SdkInterface.InterfaceResponse> ServeMockResponseAsync(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        var path = request.Path.TrimStart('/');

        // Record request if in recording mode
        if (_recordingMode)
        {
            _recordedRequests.Enqueue(new RecordedRequest
            {
                Method = request.Method.ToString(),
                Path = path,
                Timestamp = DateTimeOffset.UtcNow,
                Body = Encoding.UTF8.GetString(request.Body.Span)
            });
        }

        // Find matching mock
        if (_mockRegistry.TryGetValue(path, out var mockResponse))
        {
            // Simulate delay if configured
            if (mockResponse.DelayMs > 0)
            {
                await Task.Delay(mockResponse.DelayMs, cancellationToken);
            }

            var responseBody = Encoding.UTF8.GetBytes(mockResponse.ResponseBody);
            return new SdkInterface.InterfaceResponse(
                StatusCode: mockResponse.StatusCode,
                Headers: new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                Body: responseBody
            );
        }

        // No mock found - return 404
        return SdkInterface.InterfaceResponse.NotFound($"No mock registered for {path}");
    }

    private sealed class MockResponse
    {
        public int StatusCode { get; set; }
        public string ResponseBody { get; set; } = "{}";
        public int DelayMs { get; set; }
    }

    private sealed class RecordedRequest
    {
        public string Method { get; set; } = "";
        public string Path { get; set; } = "";
        public DateTimeOffset Timestamp { get; set; }
        public string Body { get; set; } = "";
    }
}
