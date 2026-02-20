using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Channels;

// ==================================================================================
// T90.L1: CORE INTELLIGENCE CHANNELS
// Concrete channel implementations for different communication protocols.
// ==================================================================================

#region Channel Topics

/// <summary>
/// Message bus topics for channel operations.
/// Channels use these topics to request AI capabilities from the intelligence system.
/// </summary>
public static class ChannelTopics
{
    /// <summary>Route a request through a channel.</summary>
    public const string RouteRequest = "intelligence.channel.route-request";
    public const string RouteRequestResponse = "intelligence.channel.route-request.response";

    /// <summary>Register a channel with the gateway.</summary>
    public const string RegisterChannel = "intelligence.channel.register";
    public const string RegisterChannelResponse = "intelligence.channel.register.response";

    /// <summary>Unregister a channel from the gateway.</summary>
    public const string UnregisterChannel = "intelligence.channel.unregister";
    public const string UnregisterChannelResponse = "intelligence.channel.unregister.response";

    /// <summary>Channel health check.</summary>
    public const string HealthCheck = "intelligence.channel.health-check";
    public const string HealthCheckResponse = "intelligence.channel.health-check.response";

    /// <summary>Channel metrics request.</summary>
    public const string GetMetrics = "intelligence.channel.get-metrics";
    public const string GetMetricsResponse = "intelligence.channel.get-metrics.response";

    /// <summary>Broadcast message to all channels.</summary>
    public const string Broadcast = "intelligence.channel.broadcast";

    /// <summary>Channel error notification.</summary>
    public const string ErrorNotification = "intelligence.channel.error";
}

#endregion

#region Channel Registry

/// <summary>
/// Registry for managing intelligence channels.
/// Provides centralized channel lifecycle management and discovery.
/// </summary>
/// <remarks>
/// <para>
/// The channel registry manages:
/// </para>
/// <list type="bullet">
///   <item>Channel registration and unregistration</item>
///   <item>Channel discovery by type</item>
///   <item>Broadcast messaging to all channels</item>
///   <item>Channel health monitoring</item>
/// </list>
/// </remarks>
public sealed class ChannelRegistry : IAsyncDisposable
{
    private readonly BoundedDictionary<string, IIntelligenceChannel> _channels = new BoundedDictionary<string, IIntelligenceChannel>(1000);
    private readonly BoundedDictionary<ChannelType, List<string>> _channelsByType = new BoundedDictionary<ChannelType, List<string>>(1000);
    private readonly SemaphoreSlim _registrationLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Registers a channel with the registry.
    /// </summary>
    /// <param name="channelId">Unique channel identifier.</param>
    /// <param name="channel">The channel to register.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">Thrown when channelId or channel is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a channel with the same ID already exists.</exception>
    public async Task RegisterAsync(string channelId, IIntelligenceChannel channel, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);
        ArgumentNullException.ThrowIfNull(channel);

        await _registrationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_channels.TryAdd(channelId, channel))
            {
                throw new InvalidOperationException($"Channel with ID '{channelId}' is already registered");
            }

            if (!_channelsByType.TryGetValue(channel.Type, out var typeList))
            {
                typeList = new List<string>();
                _channelsByType[channel.Type] = typeList;
            }
            typeList.Add(channelId);
        }
        finally
        {
            _registrationLock.Release();
        }
    }

    /// <summary>
    /// Unregisters a channel from the registry.
    /// </summary>
    /// <param name="channelId">The channel ID to unregister.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the channel was removed, false if not found.</returns>
    public async Task<bool> UnregisterAsync(string channelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);

        await _registrationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_channels.TryRemove(channelId, out var channel))
            {
                if (_channelsByType.TryGetValue(channel.Type, out var typeList))
                {
                    typeList.Remove(channelId);
                }
                return true;
            }
            return false;
        }
        finally
        {
            _registrationLock.Release();
        }
    }

    /// <summary>
    /// Gets a channel by ID.
    /// </summary>
    /// <param name="channelId">The channel ID.</param>
    /// <returns>The channel, or null if not found.</returns>
    public IIntelligenceChannel? GetChannel(string channelId)
    {
        return _channels.TryGetValue(channelId, out var channel) ? channel : null;
    }

    /// <summary>
    /// Gets all channels of a specific type.
    /// </summary>
    /// <param name="type">The channel type.</param>
    /// <returns>Collection of channels of the specified type.</returns>
    public IReadOnlyList<IIntelligenceChannel> GetChannelsByType(ChannelType type)
    {
        if (!_channelsByType.TryGetValue(type, out var channelIds))
        {
            return Array.Empty<IIntelligenceChannel>();
        }

        var channels = new List<IIntelligenceChannel>();
        foreach (var id in channelIds.ToArray())
        {
            if (_channels.TryGetValue(id, out var channel))
            {
                channels.Add(channel);
            }
        }
        return channels;
    }

    /// <summary>
    /// Gets all registered channel IDs.
    /// </summary>
    public IReadOnlyCollection<string> ChannelIds => _channels.Keys.ToArray();

    /// <summary>
    /// Gets the count of registered channels.
    /// </summary>
    public int Count => _channels.Count;

    /// <summary>
    /// Broadcasts a message to all channels.
    /// </summary>
    /// <param name="message">The message to broadcast.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of channels that received the message.</returns>
    public async Task<int> BroadcastAsync(ChannelMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var successCount = 0;
        var tasks = _channels.Values.Select(async channel =>
        {
            try
            {
                await channel.SendAsync(message, ct).ConfigureAwait(false);
                Interlocked.Increment(ref successCount);
            }
            catch
            {
                // Channel failed, continue with others
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return successCount;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var channel in _channels.Values)
        {
            try
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore disposal errors
            }
        }

        _channels.Clear();
        _channelsByType.Clear();
        _registrationLock.Dispose();
    }
}

#endregion

#region CLI Channel

/// <summary>
/// Command-line interface channel for terminal-based AI interactions.
/// Provides synchronous request/response patterns suitable for CLI applications.
/// </summary>
/// <remarks>
/// <para>
/// The CLI channel supports:
/// </para>
/// <list type="bullet">
///   <item>Line-based input/output processing</item>
///   <item>Command parsing and routing</item>
///   <item>Progress and status reporting</item>
///   <item>Interactive and non-interactive modes</item>
/// </list>
/// <para>
/// Messages are processed synchronously to maintain terminal output order.
/// </para>
/// </remarks>
public sealed class CLIChannel : IntelligenceChannelBase
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _errorOutput;
    private readonly CLIChannelOptions _options;
    private readonly ConcurrentQueue<ChannelMessage> _pendingMessages = new();
    private CancellationTokenSource? _inputLoopCts;
    private Task? _inputLoopTask;
    private bool _connected;

    /// <inheritdoc/>
    public override ChannelType Type => ChannelType.CLI;

    /// <summary>
    /// Gets the channel identifier.
    /// </summary>
    public string ChannelId { get; }

    /// <summary>
    /// Creates a new CLI channel with specified I/O streams.
    /// </summary>
    /// <param name="channelId">Unique channel identifier.</param>
    /// <param name="input">Input reader for receiving commands.</param>
    /// <param name="output">Output writer for responses.</param>
    /// <param name="errorOutput">Error output writer.</param>
    /// <param name="options">Channel configuration options.</param>
    public CLIChannel(
        string channelId,
        TextReader input,
        TextWriter output,
        TextWriter? errorOutput = null,
        CLIChannelOptions? options = null)
        : base(options?.QueueCapacity ?? 1000)
    {
        ChannelId = channelId ?? throw new ArgumentNullException(nameof(channelId));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _errorOutput = errorOutput ?? output;
        _options = options ?? new CLIChannelOptions();
    }

    /// <summary>
    /// Creates a new CLI channel using standard console I/O.
    /// </summary>
    /// <param name="channelId">Unique channel identifier.</param>
    /// <param name="options">Channel configuration options.</param>
    public CLIChannel(string channelId, CLIChannelOptions? options = null)
        : this(channelId, Console.In, Console.Out, Console.Error, options)
    {
    }

    /// <inheritdoc/>
    protected override Task ConnectCoreAsync(CancellationToken ct)
    {
        _connected = true;

        if (_options.InteractiveMode)
        {
            _inputLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _inputLoopTask = Task.Run(() => InputLoopAsync(_inputLoopCts.Token), _inputLoopCts.Token);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        _connected = false;

        if (_inputLoopCts != null)
        {
            await _inputLoopCts.CancelAsync();
            _inputLoopCts.Dispose();
            _inputLoopCts = null;
        }

        if (_inputLoopTask != null)
        {
            try
            {
                await _inputLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }
    }

    /// <inheritdoc/>
    protected override async Task TransmitMessageAsync(ChannelMessage message, CancellationToken ct)
    {
        var output = message.Type == ChannelMessageType.Error ? _errorOutput : _output;

        if (_options.ShowTimestamp)
        {
            await output.WriteAsync($"[{message.Timestamp:HH:mm:ss}] ").ConfigureAwait(false);
        }

        if (_options.ShowMessageType)
        {
            await output.WriteAsync($"[{message.Type}] ").ConfigureAwait(false);
        }

        await output.WriteLineAsync(message.Content).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a line from the CLI input.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The input line, or null if end of input.</returns>
    public async Task<string?> ReadLineAsync(CancellationToken ct = default)
    {
        if (!_connected)
        {
            throw new ChannelClosedException("CLI channel is not connected");
        }

        if (_options.ShowPrompt && !string.IsNullOrEmpty(_options.PromptText))
        {
            await _output.WriteAsync(_options.PromptText).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }

        return await Task.Run(() => _input.ReadLine(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a line to the CLI output.
    /// </summary>
    /// <param name="text">The text to write.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteLineAsync(string text, CancellationToken ct = default)
    {
        if (!_connected)
        {
            throw new ChannelClosedException("CLI channel is not connected");
        }

        await _output.WriteLineAsync(text).ConfigureAwait(false);
        await _output.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes an error to the CLI error output.
    /// </summary>
    /// <param name="error">The error message to write.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteErrorAsync(string error, CancellationToken ct = default)
    {
        if (!_connected)
        {
            throw new ChannelClosedException("CLI channel is not connected");
        }

        await _errorOutput.WriteLineAsync($"Error: {error}").ConfigureAwait(false);
        await _errorOutput.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task InputLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _connected)
        {
            try
            {
                var line = await ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var message = new ChannelMessage
                {
                    Type = ChannelMessageType.Query,
                    Content = line,
                    Metadata = new Dictionary<string, object>
                    {
                        ["source"] = "cli",
                        ["channelId"] = ChannelId
                    }
                };

                await QueueInboundAsync(message, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await WriteErrorAsync(ex.Message, ct).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// Configuration options for CLI channel.
/// </summary>
public sealed class CLIChannelOptions
{
    /// <summary>Gets or sets whether to run in interactive mode with input loop.</summary>
    public bool InteractiveMode { get; set; } = true;

    /// <summary>Gets or sets whether to show a prompt for input.</summary>
    public bool ShowPrompt { get; set; } = true;

    /// <summary>Gets or sets the prompt text.</summary>
    public string PromptText { get; set; } = "> ";

    /// <summary>Gets or sets whether to show timestamps in output.</summary>
    public bool ShowTimestamp { get; set; }

    /// <summary>Gets or sets whether to show message types in output.</summary>
    public bool ShowMessageType { get; set; }

    /// <summary>Gets or sets the message queue capacity.</summary>
    public int QueueCapacity { get; set; } = 1000;
}

#endregion

#region REST Channel

/// <summary>
/// REST API channel for HTTP-based AI interactions.
/// Provides standard HTTP request/response patterns for web integration.
/// </summary>
/// <remarks>
/// <para>
/// The REST channel supports:
/// </para>
/// <list type="bullet">
///   <item>HTTP request handling (GET, POST, PUT, DELETE)</item>
///   <item>JSON request/response serialization</item>
///   <item>Authentication via API keys or OAuth tokens</item>
///   <item>Rate limiting and request throttling</item>
/// </list>
/// </remarks>
public sealed class RESTChannel : IntelligenceChannelBase
{
    private readonly HttpClient _httpClient;
    private readonly RESTChannelOptions _options;
    private readonly SemaphoreSlim _rateLimiter;
    private readonly BoundedDictionary<string, PendingRequest> _pendingRequests = new BoundedDictionary<string, PendingRequest>(1000);
    private bool _ownsHttpClient;

    /// <inheritdoc/>
    public override ChannelType Type => ChannelType.API;

    /// <summary>
    /// Gets the channel identifier.
    /// </summary>
    public string ChannelId { get; }

    /// <summary>
    /// Gets the base URL for REST requests.
    /// </summary>
    public Uri BaseUrl => _options.BaseUrl;

    /// <summary>
    /// Creates a new REST channel with the specified configuration.
    /// </summary>
    /// <param name="channelId">Unique channel identifier.</param>
    /// <param name="options">Channel configuration options.</param>
    /// <param name="httpClient">Optional HTTP client to use. If null, a new client is created.</param>
    public RESTChannel(string channelId, RESTChannelOptions options, HttpClient? httpClient = null)
        : base(options?.QueueCapacity ?? 1000)
    {
        ChannelId = channelId ?? throw new ArgumentNullException(nameof(channelId));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (httpClient == null)
        {
            _httpClient = new HttpClient { BaseAddress = options.BaseUrl };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            if (_httpClient.BaseAddress == null)
            {
                _httpClient.BaseAddress = options.BaseUrl;
            }
        }

        _rateLimiter = new SemaphoreSlim(options.MaxConcurrentRequests, options.MaxConcurrentRequests);

        if (!string.IsNullOrEmpty(options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add(options.ApiKeyHeader, options.ApiKey);
        }

        if (_options.RequestTimeout.HasValue)
        {
            _httpClient.Timeout = _options.RequestTimeout.Value;
        }
    }

    /// <inheritdoc/>
    protected override Task ConnectCoreAsync(CancellationToken ct)
    {
        // REST is stateless, no connection needed
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisconnectCoreAsync(CancellationToken ct)
    {
        // Cancel all pending requests
        foreach (var request in _pendingRequests.Values)
        {
            request.Cts.Cancel();
        }
        _pendingRequests.Clear();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task TransmitMessageAsync(ChannelMessage message, CancellationToken ct)
    {
        // REST responses are handled via HTTP response, not async transmission
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends a POST request to the intelligence API.
    /// </summary>
    /// <typeparam name="TRequest">Request type.</typeparam>
    /// <typeparam name="TResponse">Response type.</typeparam>
    /// <param name="endpoint">API endpoint path.</param>
    /// <param name="request">Request payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The API response.</returns>
    public async Task<TResponse?> PostAsync<TRequest, TResponse>(string endpoint, TRequest request, CancellationToken ct = default)
        where TRequest : class
        where TResponse : class
    {
        await _rateLimiter.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await _httpClient.PostAsJsonAsync(endpoint, request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<TResponse>(ct).ConfigureAwait(false);
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    /// <summary>
    /// Sends a GET request to the intelligence API.
    /// </summary>
    /// <typeparam name="TResponse">Response type.</typeparam>
    /// <param name="endpoint">API endpoint path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The API response.</returns>
    public async Task<TResponse?> GetAsync<TResponse>(string endpoint, CancellationToken ct = default)
        where TResponse : class
    {
        await _rateLimiter.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _httpClient.GetFromJsonAsync<TResponse>(endpoint, ct).ConfigureAwait(false);
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    /// <summary>
    /// Sends an intelligence request via REST.
    /// </summary>
    /// <param name="request">The intelligence request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The intelligence response.</returns>
    public async Task<IntelligenceResponse> SendRequestAsync(IntelligenceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _rateLimiter.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await _httpClient.PostAsJsonAsync(_options.IntelligenceEndpoint, request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return IntelligenceResponse.CreateFailure(request.RequestId, errorContent, response.StatusCode.ToString());
            }

            var result = await response.Content.ReadFromJsonAsync<IntelligenceResponse>(ct).ConfigureAwait(false);
            return result ?? IntelligenceResponse.CreateFailure(request.RequestId, "Empty response", "EMPTY_RESPONSE");
        }
        catch (HttpRequestException ex)
        {
            return IntelligenceResponse.CreateFailure(request.RequestId, ex.Message, "HTTP_ERROR");
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    /// <summary>
    /// Streams an intelligence response via REST.
    /// </summary>
    /// <param name="request">The intelligence request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of response chunks.</returns>
    public async IAsyncEnumerable<IntelligenceChunk> StreamRequestAsync(
        IntelligenceRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.EnableStreaming = true;

        await _rateLimiter.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.IntelligenceEndpoint)
            {
                Content = JsonContent.Create(request)
            };

            var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            var index = 0;
            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                // Parse Server-Sent Events format
                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    var data = line.Substring(6);
                    if (data == "[DONE]")
                    {
                        yield return new IntelligenceChunk { Index = index, IsFinal = true, FinishReason = "stop" };
                        yield break;
                    }

                    var chunk = JsonSerializer.Deserialize<IntelligenceChunk>(data);
                    if (chunk != null)
                    {
                        chunk.Index = index++;
                        yield return chunk;
                    }
                }
            }
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
        _rateLimiter.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class PendingRequest
    {
        public required string RequestId { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required TaskCompletionSource<IntelligenceResponse> Tcs { get; init; }
    }
}

/// <summary>
/// Configuration options for REST channel.
/// </summary>
public sealed class RESTChannelOptions
{
    /// <summary>Gets or sets the base URL for the REST API.</summary>
    public required Uri BaseUrl { get; set; }

    /// <summary>Gets or sets the intelligence endpoint path.</summary>
    public string IntelligenceEndpoint { get; set; } = "/api/intelligence/process";

    /// <summary>Gets or sets the API key for authentication.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Gets or sets the header name for the API key.</summary>
    public string ApiKeyHeader { get; set; } = "X-API-Key";

    /// <summary>Gets or sets the maximum concurrent requests.</summary>
    public int MaxConcurrentRequests { get; set; } = 10;

    /// <summary>Gets or sets the request timeout.</summary>
    public TimeSpan? RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the message queue capacity.</summary>
    public int QueueCapacity { get; set; } = 1000;
}

#endregion

#region gRPC Channel

/// <summary>
/// gRPC channel for high-performance service-to-service AI interactions.
/// Provides bidirectional streaming and efficient binary serialization.
/// </summary>
/// <remarks>
/// <para>
/// The gRPC channel supports:
/// </para>
/// <list type="bullet">
///   <item>Unary (request/response) operations</item>
///   <item>Server streaming</item>
///   <item>Client streaming</item>
///   <item>Bidirectional streaming</item>
/// </list>
/// <para>
/// This implementation provides a protocol-agnostic abstraction that can be
/// connected to actual gRPC infrastructure via dependency injection.
/// </para>
/// </remarks>
public sealed class GRPCChannel : IntelligenceChannelBase
{
    private readonly GRPCChannelOptions _options;
    private readonly BoundedDictionary<string, GRPCCallState> _activeCalls = new BoundedDictionary<string, GRPCCallState>(1000);
    private IGRPCClientAdapter? _clientAdapter;
    private Task? _streamingTask;
    private CancellationTokenSource? _streamingCts;

    /// <inheritdoc/>
    public override ChannelType Type => ChannelType.Grpc;

    /// <summary>
    /// Gets the channel identifier.
    /// </summary>
    public string ChannelId { get; }

    /// <summary>
    /// Gets the target endpoint for gRPC calls.
    /// </summary>
    public string Endpoint => _options.Endpoint;

    /// <summary>
    /// Creates a new gRPC channel.
    /// </summary>
    /// <param name="channelId">Unique channel identifier.</param>
    /// <param name="options">Channel configuration options.</param>
    /// <param name="clientAdapter">Optional gRPC client adapter for actual gRPC operations.</param>
    public GRPCChannel(string channelId, GRPCChannelOptions options, IGRPCClientAdapter? clientAdapter = null)
        : base(options?.QueueCapacity ?? 1000)
    {
        ChannelId = channelId ?? throw new ArgumentNullException(nameof(channelId));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clientAdapter = clientAdapter;

        HeartbeatEnabled = options.EnableKeepAlive;
        HeartbeatInterval = options.KeepAliveInterval;
    }

    /// <summary>
    /// Sets the gRPC client adapter for actual gRPC operations.
    /// </summary>
    /// <param name="adapter">The client adapter.</param>
    public void SetClientAdapter(IGRPCClientAdapter adapter)
    {
        _clientAdapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    /// <inheritdoc/>
    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        if (_clientAdapter != null)
        {
            await _clientAdapter.ConnectAsync(_options.Endpoint, ct).ConfigureAwait(false);
        }

        _streamingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    /// <inheritdoc/>
    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        if (_streamingCts != null)
        {
            await _streamingCts.CancelAsync();
            _streamingCts.Dispose();
            _streamingCts = null;
        }

        // Complete all active calls
        foreach (var call in _activeCalls.Values)
        {
            call.Cts.Cancel();
        }
        _activeCalls.Clear();

        if (_clientAdapter != null)
        {
            await _clientAdapter.DisconnectAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    protected override async Task TransmitMessageAsync(ChannelMessage message, CancellationToken ct)
    {
        if (_clientAdapter != null)
        {
            await _clientAdapter.SendMessageAsync(message, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends a unary gRPC request.
    /// </summary>
    /// <param name="request">The intelligence request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The intelligence response.</returns>
    public async Task<IntelligenceResponse> SendUnaryAsync(IntelligenceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_clientAdapter == null)
        {
            return IntelligenceResponse.CreateFailure(request.RequestId, "gRPC client adapter not configured", "NO_ADAPTER");
        }

        var callState = new GRPCCallState
        {
            CallId = request.RequestId,
            Cts = CancellationTokenSource.CreateLinkedTokenSource(ct),
            StartTime = DateTime.UtcNow
        };
        _activeCalls[request.RequestId] = callState;

        try
        {
            return await _clientAdapter.ProcessUnaryAsync(request, callState.Cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _activeCalls.TryRemove(request.RequestId, out _);
            callState.Cts.Dispose();
        }
    }

    /// <summary>
    /// Initiates a server streaming gRPC call.
    /// </summary>
    /// <param name="request">The intelligence request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of response chunks.</returns>
    public async IAsyncEnumerable<IntelligenceChunk> StreamServerAsync(
        IntelligenceRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_clientAdapter == null)
        {
            yield return new IntelligenceChunk { Index = 0, IsFinal = true, FinishReason = "error" };
            yield break;
        }

        var callState = new GRPCCallState
        {
            CallId = request.RequestId,
            Cts = CancellationTokenSource.CreateLinkedTokenSource(ct),
            StartTime = DateTime.UtcNow
        };
        _activeCalls[request.RequestId] = callState;

        try
        {
            await foreach (var chunk in _clientAdapter.ProcessStreamingAsync(request, callState.Cts.Token).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
        finally
        {
            _activeCalls.TryRemove(request.RequestId, out _);
            callState.Cts.Dispose();
        }
    }

    /// <inheritdoc/>
    protected override async Task OnHeartbeatAsync(CancellationToken ct)
    {
        if (_clientAdapter != null)
        {
            await _clientAdapter.SendKeepAliveAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class GRPCCallState
    {
        public required string CallId { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required DateTime StartTime { get; init; }
    }
}

/// <summary>
/// Configuration options for gRPC channel.
/// </summary>
public sealed class GRPCChannelOptions
{
    /// <summary>Gets or sets the gRPC endpoint.</summary>
    public required string Endpoint { get; set; }

    /// <summary>Gets or sets whether to use TLS.</summary>
    public bool UseTls { get; set; } = true;

    /// <summary>Gets or sets the call timeout.</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets whether to enable keep-alive.</summary>
    public bool EnableKeepAlive { get; set; } = true;

    /// <summary>Gets or sets the keep-alive interval.</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the message queue capacity.</summary>
    public int QueueCapacity { get; set; } = 1000;

    /// <summary>Gets or sets the maximum message size in bytes.</summary>
    public int MaxMessageSize { get; set; } = 4 * 1024 * 1024; // 4MB

    /// <summary>Gets or sets optional authentication metadata.</summary>
    public Dictionary<string, string> AuthMetadata { get; set; } = new();
}

/// <summary>
/// Adapter interface for actual gRPC client operations.
/// Implement this interface to connect to real gRPC infrastructure.
/// </summary>
public interface IGRPCClientAdapter
{
    /// <summary>
    /// Connects to the gRPC endpoint.
    /// </summary>
    /// <param name="endpoint">The endpoint address.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ConnectAsync(string endpoint, CancellationToken ct = default);

    /// <summary>
    /// Disconnects from the gRPC endpoint.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Sends a channel message.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendMessageAsync(ChannelMessage message, CancellationToken ct = default);

    /// <summary>
    /// Processes a unary request.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The response.</returns>
    Task<IntelligenceResponse> ProcessUnaryAsync(IntelligenceRequest request, CancellationToken ct = default);

    /// <summary>
    /// Processes a streaming request.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of chunks.</returns>
    IAsyncEnumerable<IntelligenceChunk> ProcessStreamingAsync(IntelligenceRequest request, CancellationToken ct = default);

    /// <summary>
    /// Sends a keep-alive ping.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task SendKeepAliveAsync(CancellationToken ct = default);
}

#endregion

#region WebSocket Channel

/// <summary>
/// WebSocket channel for real-time bidirectional AI interactions.
/// Provides low-latency, persistent connections for streaming and interactive use cases.
/// </summary>
/// <remarks>
/// <para>
/// The WebSocket channel supports:
/// </para>
/// <list type="bullet">
///   <item>Full-duplex communication</item>
///   <item>Automatic reconnection</item>
///   <item>Message fragmentation handling</item>
///   <item>Ping/pong keep-alive</item>
/// </list>
/// </remarks>
public sealed class WebSocketChannel : IntelligenceChannelBase
{
    private readonly WebSocketChannelOptions _options;
    private ClientWebSocket? _webSocket;
    private Task? _receiveTask;
    private CancellationTokenSource? _receiveCts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _reconnectAttempts;

    /// <inheritdoc/>
    public override ChannelType Type => ChannelType.WebSocket;

    /// <summary>
    /// Gets the channel identifier.
    /// </summary>
    public string ChannelId { get; }

    /// <summary>
    /// Gets the WebSocket endpoint URL.
    /// </summary>
    public Uri EndpointUrl => _options.EndpointUrl;

    /// <summary>
    /// Gets the current WebSocket state.
    /// </summary>
    public WebSocketState? WebSocketState => _webSocket?.State;

    /// <summary>
    /// Event raised when the WebSocket connection is established.
    /// </summary>
    public event EventHandler? Connected;

    /// <summary>
    /// Event raised when the WebSocket connection is lost.
    /// </summary>
    public event EventHandler<WebSocketCloseEventArgs>? Disconnected;

    /// <summary>
    /// Creates a new WebSocket channel.
    /// </summary>
    /// <param name="channelId">Unique channel identifier.</param>
    /// <param name="options">Channel configuration options.</param>
    public WebSocketChannel(string channelId, WebSocketChannelOptions options)
        : base(options?.QueueCapacity ?? 1000)
    {
        ChannelId = channelId ?? throw new ArgumentNullException(nameof(channelId));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        HeartbeatEnabled = options.EnablePing;
        HeartbeatInterval = options.PingInterval;
    }

    /// <inheritdoc/>
    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        _webSocket = new ClientWebSocket();

        // Set subprotocol if specified
        if (!string.IsNullOrEmpty(_options.SubProtocol))
        {
            _webSocket.Options.AddSubProtocol(_options.SubProtocol);
        }

        // Add custom headers
        foreach (var header in _options.CustomHeaders)
        {
            _webSocket.Options.SetRequestHeader(header.Key, header.Value);
        }

        await _webSocket.ConnectAsync(_options.EndpointUrl, ct).ConfigureAwait(false);

        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), _receiveCts.Token);

        _reconnectAttempts = 0;
        Connected?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        if (_receiveCts != null)
        {
            await _receiveCts.CancelAsync();
            _receiveCts.Dispose();
            _receiveCts = null;
        }

        if (_webSocket != null)
        {
            if (_webSocket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                try
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Channel closing", ct).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore close errors
                }
            }
            _webSocket.Dispose();
            _webSocket = null;
        }

        Disconnected?.Invoke(this, new WebSocketCloseEventArgs
        {
            CloseStatus = WebSocketCloseStatus.NormalClosure,
            CloseDescription = "Channel closing"
        });
    }

    /// <inheritdoc/>
    protected override async Task TransmitMessageAsync(ChannelMessage message, CancellationToken ct)
    {
        if (_webSocket?.State != System.Net.WebSockets.WebSocketState.Open)
        {
            throw new ChannelClosedException("WebSocket is not connected");
        }

        var json = JsonSerializer.Serialize(message);
        var buffer = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _webSocket.SendAsync(
                new ArraySegment<byte>(buffer),
                WebSocketMessageType.Text,
                true,
                ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Sends raw binary data through the WebSocket.
    /// </summary>
    /// <param name="data">The data to send.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_webSocket?.State != System.Net.WebSockets.WebSocketState.Open)
        {
            throw new ChannelClosedException("WebSocket is not connected");
        }

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _webSocket.SendAsync(data, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc/>
    protected override async Task OnHeartbeatAsync(CancellationToken ct)
    {
        if (_webSocket?.State == System.Net.WebSockets.WebSocketState.Open)
        {
            var pingMessage = new ChannelMessage
            {
                Type = ChannelMessageType.Control,
                Content = "ping",
                Metadata = new Dictionary<string, object> { ["type"] = "ping" }
            };
            await TransmitMessageAsync(pingMessage, ct).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_options.ReceiveBufferSize);
        try
        {
            while (!ct.IsCancellationRequested && _webSocket?.State == System.Net.WebSockets.WebSocketState.Open)
            {
                try
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Disconnected?.Invoke(this, new WebSocketCloseEventArgs
                        {
                            CloseStatus = result.CloseStatus ?? WebSocketCloseStatus.Empty,
                            CloseDescription = result.CloseStatusDescription
                        });

                        if (_options.AutoReconnect)
                        {
                            await TryReconnectAsync(ct).ConfigureAwait(false);
                        }
                        break;
                    }

                    var messageBytes = new byte[result.Count];
                    Array.Copy(buffer, messageBytes, result.Count);

                    ChannelMessage message;
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var json = Encoding.UTF8.GetString(messageBytes);
                        message = JsonSerializer.Deserialize<ChannelMessage>(json) ?? new ChannelMessage
                        {
                            Type = ChannelMessageType.Response,
                            Content = json
                        };
                    }
                    else
                    {
                        message = new ChannelMessage
                        {
                            Type = ChannelMessageType.Response,
                            BinaryPayload = messageBytes
                        };
                    }

                    await QueueInboundAsync(message, ct).ConfigureAwait(false);
                }
                catch (WebSocketException ex) when (!ct.IsCancellationRequested)
                {
                    Disconnected?.Invoke(this, new WebSocketCloseEventArgs
                    {
                        CloseStatus = WebSocketCloseStatus.ProtocolError,
                        CloseDescription = ex.Message
                    });

                    if (_options.AutoReconnect)
                    {
                        await TryReconnectAsync(ct).ConfigureAwait(false);
                    }
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task TryReconnectAsync(CancellationToken ct)
    {
        while (_reconnectAttempts < _options.MaxReconnectAttempts && !ct.IsCancellationRequested)
        {
            _reconnectAttempts++;
            var delay = TimeSpan.FromMilliseconds(_options.ReconnectDelayMs * Math.Pow(2, _reconnectAttempts - 1));

            await Task.Delay(delay, ct).ConfigureAwait(false);

            try
            {
                _webSocket?.Dispose();
                _webSocket = new ClientWebSocket();

                if (!string.IsNullOrEmpty(_options.SubProtocol))
                {
                    _webSocket.Options.AddSubProtocol(_options.SubProtocol);
                }

                foreach (var header in _options.CustomHeaders)
                {
                    _webSocket.Options.SetRequestHeader(header.Key, header.Value);
                }

                await _webSocket.ConnectAsync(_options.EndpointUrl, ct).ConfigureAwait(false);

                _reconnectAttempts = 0;
                Connected?.Invoke(this, EventArgs.Empty);
                return;
            }
            catch
            {
                // Continue to next attempt
            }
        }
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        _sendLock.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Configuration options for WebSocket channel.
/// </summary>
public sealed class WebSocketChannelOptions
{
    /// <summary>Gets or sets the WebSocket endpoint URL.</summary>
    public required Uri EndpointUrl { get; set; }

    /// <summary>Gets or sets the optional subprotocol.</summary>
    public string? SubProtocol { get; set; }

    /// <summary>Gets or sets custom headers.</summary>
    public Dictionary<string, string> CustomHeaders { get; set; } = new();

    /// <summary>Gets or sets the receive buffer size.</summary>
    public int ReceiveBufferSize { get; set; } = 8192;

    /// <summary>Gets or sets whether to enable ping/pong.</summary>
    public bool EnablePing { get; set; } = true;

    /// <summary>Gets or sets the ping interval.</summary>
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets whether to auto-reconnect.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Gets or sets the reconnect delay in milliseconds.</summary>
    public int ReconnectDelayMs { get; set; } = 1000;

    /// <summary>Gets or sets the maximum reconnect attempts.</summary>
    public int MaxReconnectAttempts { get; set; } = 5;

    /// <summary>Gets or sets the message queue capacity.</summary>
    public int QueueCapacity { get; set; } = 1000;
}

/// <summary>
/// Event arguments for WebSocket close events.
/// </summary>
public sealed class WebSocketCloseEventArgs : EventArgs
{
    /// <summary>Gets or sets the close status.</summary>
    public WebSocketCloseStatus CloseStatus { get; set; }

    /// <summary>Gets or sets the close description.</summary>
    public string? CloseDescription { get; set; }
}

#endregion

#region Plugin Channel

/// <summary>
/// Internal plugin-to-intelligence channel for intra-process AI interactions.
/// Provides high-performance, zero-copy communication within the plugin ecosystem.
/// </summary>
/// <remarks>
/// <para>
/// The Plugin channel supports:
/// </para>
/// <list type="bullet">
///   <item>Direct in-memory message passing</item>
///   <item>Message bus integration for AI capability requests</item>
///   <item>Priority-based request handling</item>
///   <item>Synchronous and asynchronous patterns</item>
/// </list>
/// <para>
/// This channel is optimized for plugin-to-intelligence communication
/// without network overhead.
/// </para>
/// </remarks>
public sealed class PluginChannel : IntelligenceChannelBase
{
    private readonly PluginChannelOptions _options;
    private readonly Channel<PrioritizedMessage> _priorityQueue;
    private readonly BoundedDictionary<string, TaskCompletionSource<IntelligenceResponse>> _pendingRequests = new BoundedDictionary<string, TaskCompletionSource<IntelligenceResponse>>(1000);
    private readonly Func<IntelligenceRequest, CancellationToken, Task<IntelligenceResponse>>? _requestHandler;
    private Task? _processingTask;
    private CancellationTokenSource? _processingCts;
    private bool _connected;

    /// <inheritdoc/>
    public override ChannelType Type => ChannelType.Custom;

    /// <summary>
    /// Gets the channel identifier.
    /// </summary>
    public string ChannelId { get; }

    /// <summary>
    /// Gets the source plugin identifier.
    /// </summary>
    public string PluginId { get; }

    /// <summary>
    /// Event raised when a request is received.
    /// </summary>
    public event Func<IntelligenceRequest, CancellationToken, Task<IntelligenceResponse>>? RequestReceived;

    /// <summary>
    /// Creates a new plugin channel.
    /// </summary>
    /// <param name="channelId">Unique channel identifier.</param>
    /// <param name="pluginId">Source plugin identifier.</param>
    /// <param name="options">Channel configuration options.</param>
    /// <param name="requestHandler">Optional request handler for processing requests.</param>
    public PluginChannel(
        string channelId,
        string pluginId,
        PluginChannelOptions? options = null,
        Func<IntelligenceRequest, CancellationToken, Task<IntelligenceResponse>>? requestHandler = null)
        : base(options?.QueueCapacity ?? 1000)
    {
        ChannelId = channelId ?? throw new ArgumentNullException(nameof(channelId));
        PluginId = pluginId ?? throw new ArgumentNullException(nameof(pluginId));
        _options = options ?? new PluginChannelOptions();
        _requestHandler = requestHandler;

        var priorityQueueOptions = new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = _options.DropOnFull ? BoundedChannelFullMode.DropOldest : BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        _priorityQueue = Channel.CreateBounded<PrioritizedMessage>(priorityQueueOptions);
    }

    /// <inheritdoc/>
    protected override Task ConnectCoreAsync(CancellationToken ct)
    {
        _connected = true;
        _processingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (_options.EnableAutoProcessing)
        {
            _processingTask = Task.Run(() => ProcessQueueAsync(_processingCts.Token), _processingCts.Token);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        _connected = false;

        if (_processingCts != null)
        {
            await _processingCts.CancelAsync();
            _processingCts.Dispose();
            _processingCts = null;
        }

        // Complete all pending requests with cancellation
        foreach (var kvp in _pendingRequests)
        {
            kvp.Value.TrySetCanceled();
        }
        _pendingRequests.Clear();
    }

    /// <inheritdoc/>
    protected override Task TransmitMessageAsync(ChannelMessage message, CancellationToken ct)
    {
        // Plugin channel transmits directly to pending request handlers
        if (message.Type == ChannelMessageType.Response &&
            message.Metadata.TryGetValue("requestId", out var requestIdObj) &&
            requestIdObj is string requestId)
        {
            if (_pendingRequests.TryRemove(requestId, out var tcs))
            {
                var response = JsonSerializer.Deserialize<IntelligenceResponse>(message.Content);
                if (response != null)
                {
                    tcs.TrySetResult(response);
                }
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends an intelligence request and waits for response.
    /// </summary>
    /// <param name="request">The intelligence request.</param>
    /// <param name="priority">Request priority (higher = more urgent).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The intelligence response.</returns>
    public async Task<IntelligenceResponse> SendRequestAsync(
        IntelligenceRequest request,
        int priority = 0,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_connected)
        {
            throw new ChannelClosedException("Plugin channel is not connected");
        }

        var tcs = new TaskCompletionSource<IntelligenceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[request.RequestId] = tcs;

        try
        {
            await _priorityQueue.Writer.WriteAsync(new PrioritizedMessage
            {
                Priority = priority,
                Request = request,
                Timestamp = DateTime.UtcNow
            }, ct).ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(_options.RequestTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            return await tcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _pendingRequests.TryRemove(request.RequestId, out _);
            throw;
        }
    }

    /// <summary>
    /// Sends an intelligence request without waiting for response.
    /// </summary>
    /// <param name="request">The intelligence request.</param>
    /// <param name="priority">Request priority.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SendFireAndForgetAsync(IntelligenceRequest request, int priority = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_connected)
        {
            throw new ChannelClosedException("Plugin channel is not connected");
        }

        await _priorityQueue.Writer.WriteAsync(new PrioritizedMessage
        {
            Priority = priority,
            Request = request,
            Timestamp = DateTime.UtcNow,
            FireAndForget = true
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the number of pending requests.
    /// </summary>
    public int PendingRequestCount => _pendingRequests.Count;

    /// <summary>
    /// Gets channel metrics.
    /// </summary>
    public PluginChannelMetrics GetMetrics()
    {
        return new PluginChannelMetrics
        {
            ChannelId = ChannelId,
            PluginId = PluginId,
            IsConnected = _connected,
            PendingRequests = _pendingRequests.Count,
            QueuedMessages = _priorityQueue.Reader.Count
        };
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        var batch = new List<PrioritizedMessage>(_options.BatchSize);

        while (!ct.IsCancellationRequested && _connected)
        {
            try
            {
                batch.Clear();

                // Read up to batch size
                while (batch.Count < _options.BatchSize && _priorityQueue.Reader.TryRead(out var msg))
                {
                    batch.Add(msg);
                }

                if (batch.Count == 0)
                {
                    // Wait for next message
                    var msg = await _priorityQueue.Reader.ReadAsync(ct).ConfigureAwait(false);
                    batch.Add(msg);
                }

                // Sort by priority (descending)
                batch.Sort((a, b) => b.Priority.CompareTo(a.Priority));

                // Process batch
                foreach (var msg in batch)
                {
                    try
                    {
                        IntelligenceResponse response;

                        if (_requestHandler != null)
                        {
                            response = await _requestHandler(msg.Request, ct).ConfigureAwait(false);
                        }
                        else if (RequestReceived != null)
                        {
                            response = await RequestReceived(msg.Request, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            response = IntelligenceResponse.CreateFailure(msg.Request.RequestId, "No handler configured", "NO_HANDLER");
                        }

                        if (!msg.FireAndForget && _pendingRequests.TryRemove(msg.Request.RequestId, out var tcs))
                        {
                            tcs.TrySetResult(response);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!msg.FireAndForget && _pendingRequests.TryRemove(msg.Request.RequestId, out var tcs))
                        {
                            tcs.TrySetResult(IntelligenceResponse.CreateFailure(msg.Request.RequestId, ex.Message, "PROCESSING_ERROR"));
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private sealed class PrioritizedMessage
    {
        public int Priority { get; init; }
        public required IntelligenceRequest Request { get; init; }
        public DateTime Timestamp { get; init; }
        public bool FireAndForget { get; init; }
    }
}

/// <summary>
/// Configuration options for plugin channel.
/// </summary>
public sealed class PluginChannelOptions
{
    /// <summary>Gets or sets the message queue capacity.</summary>
    public int QueueCapacity { get; set; } = 1000;

    /// <summary>Gets or sets the request timeout.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets whether to enable auto-processing.</summary>
    public bool EnableAutoProcessing { get; set; } = true;

    /// <summary>Gets or sets the batch size for processing.</summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>Gets or sets whether to drop messages when full.</summary>
    public bool DropOnFull { get; set; }
}

/// <summary>
/// Metrics for plugin channel.
/// </summary>
public sealed class PluginChannelMetrics
{
    /// <summary>Gets or sets the channel ID.</summary>
    public required string ChannelId { get; init; }

    /// <summary>Gets or sets the plugin ID.</summary>
    public required string PluginId { get; init; }

    /// <summary>Gets or sets whether the channel is connected.</summary>
    public bool IsConnected { get; init; }

    /// <summary>Gets or sets the pending request count.</summary>
    public int PendingRequests { get; init; }

    /// <summary>Gets or sets the queued message count.</summary>
    public int QueuedMessages { get; init; }
}

#endregion
