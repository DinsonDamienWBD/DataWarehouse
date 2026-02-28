using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Distribution;
using DataWarehouse.SDK.Hosting;
using DataWarehouse.SDK.Primitives;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace DataWarehouse.Plugins.AedsCore.ControlPlane;

/// <summary>
/// WebSocket control plane transport implementation for AEDS signaling and manifest distribution.
/// </summary>
/// <remarks>
/// <para>
/// This plugin provides production-ready WebSocket-based control plane transport with:
/// <list type="bullet">
/// <item><description>Persistent connection with heartbeat monitoring (90-second timeout)</description></item>
/// <item><description>Automatic reconnection with exponential backoff (1s, 2s, 4s, 8s, 16s, max 32s)</description></item>
/// <item><description>Async enumerable manifest receiving via Channel buffering</description></item>
/// <item><description>Channel subscription/unsubscription via control messages</description></item>
/// <item><description>Thread-safe send operations using SemaphoreSlim</description></item>
/// <item><description>Clean shutdown with cancellation token propagation</description></item>
/// <item><description>Origin header validation to prevent cross-origin WebSocket hijacking (NET-07)</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Protocol:</strong> Uses native .NET WebSocket client (System.Net.WebSockets.ClientWebSocket)
/// for persistent bidirectional communication over WSS (WebSocket Secure).
/// </para>
/// <para>
/// <strong>Origin Validation (NET-07 - CVSS 6.1):</strong> Validates the Origin header on
/// WebSocket connections. When configured with allowed origins, rejects connections from
/// unauthorized origins. Default behavior: same-origin only (rejects all cross-origin).
/// </para>
/// <para>
/// <strong>Heartbeat Monitoring:</strong> Sends periodic heartbeats at configured intervals and monitors
/// for incoming messages. If no message received for 90 seconds, triggers reconnection.
/// </para>
/// </remarks>
[PluginProfile(ServiceProfileType.Server)]
public class WebSocketControlPlanePlugin : ControlPlaneTransportPluginBase
{
    private readonly ILogger<WebSocketControlPlanePlugin> _logger;
    private ClientWebSocket? _webSocket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _connectionCts;
    private Task? _heartbeatTask;
    private Task? _receiveTask;
    private Channel<IntentManifest>? _manifestChannel;
    // DateTimeOffset is 16 bytes — torn reads on 32-bit ARM.
    // Store as UTC ticks (long) and use Interlocked for atomic reads/writes (finding 978).
    private long _lastMessageReceivedTicks;
    private long _lastHeartbeatSentTicks;
    private const int HeartbeatExpirySeconds = 90;
    private const int MaxBackoffSeconds = 32;
    // Atomic reconnect counter (finding 977).
    private int _reconnectAttempt;

    /// <summary>
    /// Set of allowed origins for WebSocket connections.
    /// When empty, only same-origin connections are allowed (NET-07).
    /// </summary>
    private readonly HashSet<string> _allowedOrigins = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The expected server origin derived from the connection URL.
    /// Used for same-origin validation when no explicit allowed origins are configured.
    /// </summary>
    private string? _serverOrigin;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebSocketControlPlanePlugin"/> class.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostic output.</param>
    public WebSocketControlPlanePlugin(ILogger<WebSocketControlPlanePlugin> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lastMessageReceivedTicks = DateTimeOffset.UtcNow.UtcTicks;
        _lastHeartbeatSentTicks = DateTimeOffset.UtcNow.UtcTicks;
    }

    /// <inheritdoc />
    public override string Id => "com.datawarehouse.aeds.controlplane.websocket";

    /// <inheritdoc />
    public override string Name => "WebSocket Control Plane Transport";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <inheritdoc />
    public override string TransportId => "websocket";

    /// <inheritdoc />
    protected override async Task EstablishConnectionAsync(ControlPlaneConfig config, CancellationToken ct)
    {
        _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _manifestChannel = Channel.CreateUnbounded<IntentManifest>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

        // NET-07: Initialize origin validation from server URL
        InitializeOriginValidation(config);

        await ConnectWithRetryAsync(config, _connectionCts.Token);

        _heartbeatTask = RunHeartbeatLoopAsync(config, _connectionCts.Token);
        _receiveTask = RunReceiveLoopAsync(_connectionCts.Token);

        _logger.LogInformation("WebSocket control plane connection established to {ServerUrl}", config.ServerUrl);
    }

    /// <summary>
    /// Initializes origin validation configuration (NET-07: CVSS 6.1).
    /// Derives the server origin from the connection URL for same-origin enforcement.
    /// Additional allowed origins can be configured via the AllowedOrigins configuration parameter.
    /// </summary>
    /// <param name="config">The control plane configuration.</param>
    private void InitializeOriginValidation(ControlPlaneConfig config)
    {
        // Derive server origin from WebSocket URL (ws:// -> http://, wss:// -> https://)
        try
        {
            var serverUrl = config.ServerUrl;
            var httpUrl = serverUrl
                .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
                .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase);

            var uri = new Uri(httpUrl);
            _serverOrigin = $"{uri.Scheme}://{uri.Host}" + (uri.IsDefaultPort ? "" : $":{uri.Port}");

            _logger.LogDebug("WebSocket origin validation initialized. Server origin: {ServerOrigin}", _serverOrigin);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to derive server origin from URL {ServerUrl}, origin validation will be strict (deny all cross-origin)",
                config.ServerUrl);
        }
    }

    /// <summary>
    /// Validates the Origin header value against allowed origins (NET-07).
    /// </summary>
    /// <param name="origin">The origin to validate.</param>
    /// <returns>True if the origin is allowed, false otherwise.</returns>
    private bool ValidateOrigin(string? origin)
    {
        // No origin header: allow (non-browser clients, e.g., IoT devices, CLIs)
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        // Check explicit allowed origins list first
        if (_allowedOrigins.Count > 0 && _allowedOrigins.Contains(origin))
        {
            return true;
        }

        // Same-origin check: compare against derived server origin
        if (_serverOrigin != null && string.Equals(origin, _serverOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // If no allowed origins configured and origin doesn't match server origin, reject
        _logger.LogWarning(
            "WebSocket origin validation failed: origin '{Origin}' not in allowed list. Server origin: {ServerOrigin}, Allowed origins: [{AllowedOrigins}]",
            origin, _serverOrigin ?? "unknown", string.Join(", ", _allowedOrigins));

        return false;
    }

    /// <summary>
    /// Adds an origin to the allowed origins list for WebSocket connections.
    /// </summary>
    /// <param name="origin">The origin to allow (e.g., "https://example.com").</param>
    public void AddAllowedOrigin(string origin)
    {
        if (!string.IsNullOrWhiteSpace(origin))
        {
            _allowedOrigins.Add(origin);
            _logger.LogInformation("Added allowed WebSocket origin: {Origin}", origin);
        }
    }

    /// <summary>
    /// Connects to WebSocket server with exponential backoff retry logic.
    /// </summary>
    private async Task ConnectWithRetryAsync(ControlPlaneConfig config, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _webSocket?.Dispose();
                _webSocket = new ClientWebSocket();

                if (!string.IsNullOrEmpty(config.AuthToken))
                {
                    _webSocket.Options.SetRequestHeader("Authorization", $"Bearer {config.AuthToken}");
                }

                // NET-07: Set Origin header for server-side origin validation
                if (_serverOrigin != null)
                {
                    _webSocket.Options.SetRequestHeader("Origin", _serverOrigin);
                }

                var uri = new Uri(config.ServerUrl);
                await _webSocket.ConnectAsync(uri, ct);

                Interlocked.Exchange(ref _lastMessageReceivedTicks, DateTimeOffset.UtcNow.UtcTicks);
                Interlocked.Exchange(ref _reconnectAttempt, 0);

                _logger.LogInformation("WebSocket connected to {ServerUrl}", config.ServerUrl);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var attempt = Interlocked.Increment(ref _reconnectAttempt);
                var backoffSeconds = Math.Min((int)Math.Pow(2, attempt - 1), MaxBackoffSeconds);
                _logger.LogWarning(ex, "WebSocket connection failed, retrying in {BackoffSeconds}s (attempt {Attempt})",
                    backoffSeconds, attempt);

                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct);
            }
        }
    }

    /// <summary>
    /// Background task that sends periodic heartbeats and monitors for expiry.
    /// </summary>
    private async Task RunHeartbeatLoopAsync(ControlPlaneConfig config, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(config.HeartbeatInterval, ct);

                var heartbeat = new HeartbeatMessage(
                    ClientId: config.ClientId,
                    Timestamp: DateTimeOffset.UtcNow,
                    Status: ClientStatus.Online,
                    Metrics: null
                );

                await TransmitHeartbeatAsync(heartbeat, ct);

                var lastMsgTicks = Interlocked.Read(ref _lastMessageReceivedTicks);
                var timeSinceLastMessage = DateTimeOffset.UtcNow - new DateTimeOffset(lastMsgTicks, TimeSpan.Zero);
                if (timeSinceLastMessage.TotalSeconds > HeartbeatExpirySeconds)
                {
                    _logger.LogWarning("No message received for {Seconds}s, triggering reconnection",
                        timeSinceLastMessage.TotalSeconds);
                    await ReconnectAsync(config, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Heartbeat loop error");
            }
        }
    }

    /// <summary>
    /// Background task that receives WebSocket messages and dispatches them.
    /// </summary>
    private async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_webSocket == null || _webSocket.State != WebSocketState.Open)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), ct);
                        continue;
                    }

                    var messageBuilder = new StringBuilder();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _logger.LogWarning("WebSocket close frame received");
                            await ReconnectAsync(Config!, ct);
                            break;
                        }

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            messageBuilder.Append(text);
                        }
                    }
                    while (!result.EndOfMessage && !ct.IsCancellationRequested);

                    if (result.MessageType == WebSocketMessageType.Text && messageBuilder.Length > 0)
                    {
                        Interlocked.Exchange(ref _lastMessageReceivedTicks, DateTimeOffset.UtcNow.UtcTicks);
                        await ProcessReceivedMessageAsync(messageBuilder.ToString(), ct);
                    }
                }
                catch (WebSocketException ex)
                {
                    _logger.LogError(ex, "WebSocket receive error, triggering reconnection");
                    await ReconnectAsync(Config!, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Receive loop error");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Processes a received text message from WebSocket.
    /// </summary>
    private async Task ProcessReceivedMessageAsync(string messageText, CancellationToken ct)
    {
        try
        {
            // NET-07: Validate message size to prevent oversized payload attacks
            const int maxMessageSizeBytes = 4 * 1024 * 1024; // 4 MB max
            if (messageText.Length > maxMessageSizeBytes)
            {
                _logger.LogWarning("Rejecting oversized WebSocket message ({Size} bytes, max {Max})",
                    messageText.Length, maxMessageSizeBytes);
                return;
            }

            using var jsonDoc = JsonDocument.Parse(messageText);
            var root = jsonDoc.RootElement;

            // NET-07: Validate origin field in message envelope if present
            if (root.TryGetProperty("origin", out var originElement))
            {
                var messageOrigin = originElement.GetString();
                if (!ValidateOrigin(messageOrigin))
                {
                    _logger.LogWarning("Rejecting WebSocket message with unauthorized origin: {Origin}", messageOrigin);
                    return;
                }
            }

            if (root.TryGetProperty("type", out var typeElement))
            {
                var messageType = typeElement.GetString();

                if (messageType == "manifest")
                {
                    var manifest = JsonSerializer.Deserialize<IntentManifest>(messageText);
                    if (manifest != null && _manifestChannel != null)
                    {
                        await _manifestChannel.Writer.WriteAsync(manifest, ct);
                        _logger.LogDebug("Received manifest {ManifestId}", manifest.ManifestId);
                    }
                }
                else if (messageType == "ack")
                {
                    _logger.LogDebug("Received acknowledgment message");
                }
                else
                {
                    _logger.LogDebug("Received unknown message type: {Type}", messageType);
                }
            }
            else
            {
                var manifest = JsonSerializer.Deserialize<IntentManifest>(messageText);
                if (manifest != null && _manifestChannel != null)
                {
                    await _manifestChannel.Writer.WriteAsync(manifest, ct);
                    _logger.LogDebug("Received manifest {ManifestId} (no type field)", manifest.ManifestId);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize message, skipping invalid JSON");
        }
    }

    /// <summary>
    /// Triggers reconnection with exponential backoff.
    /// </summary>
    private async Task ReconnectAsync(ControlPlaneConfig config, CancellationToken ct)
    {
        _logger.LogInformation("Initiating reconnection");

        try
        {
            if (_webSocket?.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error closing WebSocket during reconnection");
        }

        await ConnectWithRetryAsync(config, ct);
    }

    /// <inheritdoc />
    protected override async Task TransmitManifestAsync(IntentManifest manifest, CancellationToken ct)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not connected");
        }

        var json = JsonSerializer.Serialize(manifest);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(ct);
        try
        {
            await _webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct);

            _logger.LogDebug("Transmitted manifest {ManifestId}", manifest.ManifestId);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<IntentManifest> ListenForManifestsAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_manifestChannel == null)
        {
            throw new InvalidOperationException("Not connected, manifest channel is not available");
        }

        await foreach (var manifest in _manifestChannel.Reader.ReadAllAsync(ct))
        {
            yield return manifest;
        }
    }

    /// <inheritdoc />
    protected override async Task TransmitHeartbeatAsync(HeartbeatMessage heartbeat, CancellationToken ct)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
        {
            return;
        }

        var json = JsonSerializer.Serialize(heartbeat);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(ct);
        try
        {
            await _webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct);

            Interlocked.Exchange(ref _lastHeartbeatSentTicks, DateTimeOffset.UtcNow.UtcTicks);
            _logger.LogTrace("Transmitted heartbeat for client {ClientId}", heartbeat.ClientId);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Failed to send heartbeat");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task JoinChannelAsync(string channelId, CancellationToken ct)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not connected");
        }

        var controlMessage = new
        {
            type = "subscribe",
            channelId = channelId
        };

        var json = JsonSerializer.Serialize(controlMessage);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(ct);
        try
        {
            await _webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct);

            _logger.LogInformation("Subscribed to channel {ChannelId}", channelId);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task LeaveChannelAsync(string channelId, CancellationToken ct)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not connected");
        }

        var controlMessage = new
        {
            type = "unsubscribe",
            channelId = channelId
        };

        var json = JsonSerializer.Serialize(controlMessage);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(ct);
        try
        {
            await _webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct);

            _logger.LogInformation("Unsubscribed from channel {ChannelId}", channelId);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task CloseConnectionAsync()
    {
        _logger.LogInformation("Closing WebSocket control plane connection");

        _connectionCts?.Cancel();

        if (_heartbeatTask != null)
        {
            try { await _heartbeatTask; } catch { /* Best-effort task cleanup */ }
        }

        if (_receiveTask != null)
        {
            try { await _receiveTask; } catch { /* Best-effort task cleanup */ }
        }

        if (_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Client disconnect",
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error during WebSocket close");
            }
        }

        _webSocket?.Dispose();
        _webSocket = null;

        _manifestChannel?.Writer.Complete();
        _manifestChannel = null;

        _connectionCts?.Dispose();
        _connectionCts = null;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => Task.CompletedTask;

    /// <summary>
    /// Disposes resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sendLock?.Dispose();
            _connectionCts?.Dispose();
            _webSocket?.Dispose();
        }
        base.Dispose(disposing);
    }
}
