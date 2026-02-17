using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Distribution;
using DataWarehouse.SDK.Hosting;
using DataWarehouse.SDK.Primitives;
using Microsoft.Extensions.Logging;
using Grpc.Core;
using Grpc.Net.Client;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace DataWarehouse.Plugins.AedsCore.ControlPlane;

/// <summary>
/// gRPC control plane transport implementation for AEDS signaling and manifest distribution.
/// </summary>
/// <remarks>
/// <para>
/// This plugin provides production-ready gRPC streaming-based control plane transport with:
/// <list type="bullet">
/// <item><description>Bidirectional streaming for full-duplex communication</description></item>
/// <item><description>JSON-over-gRPC encoding (no protobuf compilation required for Phase 12)</description></item>
/// <item><description>Manual reconnection with exponential backoff (1s, 2s, 4s, 8s, 16s, max 32s)</description></item>
/// <item><description>Deadline propagation for timeout handling</description></item>
/// <item><description>Channel management with proper lifecycle and cleanup</description></item>
/// <item><description>Async enumerable manifest receiving via stream reading</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Protocol:</strong> Uses gRPC bidirectional streaming (DuplexStreaming) with generic
/// byte payload wrapper. JSON serialization keeps implementation simple while maintaining
/// type safety via SDK contracts.
/// </para>
/// <para>
/// <strong>Use Cases:</strong> Ideal for high-performance RPC-heavy workloads, microservice
/// architectures, and scenarios requiring low latency with HTTP/2 multiplexing.
/// </para>
/// </remarks>
[PluginProfile(ServiceProfileType.Server)]
public class GrpcControlPlanePlugin : ControlPlaneTransportPluginBase
{
    private readonly ILogger<GrpcControlPlanePlugin> _logger;
    private GrpcChannel? _channel;
    private AsyncDuplexStreamingCall<ControlMessage, ControlMessage>? _streamCall;
    private CancellationTokenSource? _connectionCts;
    private Task? _heartbeatTask;
    private Task? _receiveTask;
    private Channel<IntentManifest>? _manifestChannel;
    private const int MaxBackoffSeconds = 32;
    private int _reconnectAttempt;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="GrpcControlPlanePlugin"/> class.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostic output.</param>
    public GrpcControlPlanePlugin(ILogger<GrpcControlPlanePlugin> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override string Id => "com.datawarehouse.aeds.controlplane.grpc";

    /// <inheritdoc />
    public override string Name => "gRPC Control Plane Transport";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <inheritdoc />
    public override string TransportId => "grpc";

    /// <inheritdoc />
    protected override async Task EstablishConnectionAsync(ControlPlaneConfig config, CancellationToken ct)
    {
        _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _manifestChannel = Channel.CreateUnbounded<IntentManifest>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

        await ConnectWithRetryAsync(config, _connectionCts.Token);

        _heartbeatTask = RunHeartbeatLoopAsync(config, _connectionCts.Token);
        _receiveTask = RunReceiveLoopAsync(_connectionCts.Token);

        _logger.LogInformation("gRPC control plane connection established to {ServerUrl}", config.ServerUrl);
    }

    /// <summary>
    /// Connects to gRPC server with exponential backoff retry logic.
    /// </summary>
    private async Task ConnectWithRetryAsync(ControlPlaneConfig config, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _channel?.Dispose();
                _channel = GrpcChannel.ForAddress(config.ServerUrl, new GrpcChannelOptions
                {
                    Credentials = Grpc.Core.ChannelCredentials.Insecure,
                    MaxReceiveMessageSize = 10 * 1024 * 1024,
                    MaxSendMessageSize = 10 * 1024 * 1024
                });

                var client = new AedsControlPlane.AedsControlPlaneClient(_channel);

                var metadata = new Metadata();
                if (!string.IsNullOrEmpty(config.AuthToken))
                {
                    metadata.Add("Authorization", $"Bearer {config.AuthToken}");
                }

                _streamCall = client.StreamManifests(metadata, cancellationToken: ct);

                _reconnectAttempt = 0;
                _logger.LogInformation("gRPC stream established to {ServerUrl}", config.ServerUrl);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var backoffSeconds = Math.Min((int)Math.Pow(2, _reconnectAttempt), MaxBackoffSeconds);
                _logger.LogWarning(ex, "gRPC connection failed, retrying in {BackoffSeconds}s (attempt {Attempt})",
                    backoffSeconds, _reconnectAttempt + 1);

                _reconnectAttempt++;
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct);
            }
        }
    }

    /// <summary>
    /// Background task that sends periodic heartbeats.
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
    /// Background task that receives gRPC stream messages.
    /// </summary>
    private async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_streamCall == null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    continue;
                }

                await foreach (var message in _streamCall.ResponseStream.ReadAllAsync(ct))
                {
                    await ProcessReceivedMessageAsync(message, ct);
                }

                _logger.LogWarning("gRPC stream ended, reconnecting");
                await ReconnectAsync(Config!, ct);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
            {
                _logger.LogWarning("gRPC server unavailable, reconnecting");
                await ReconnectAsync(Config!, ct);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
            {
                _logger.LogInformation("gRPC stream cancelled");
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Receive loop error, reconnecting");
                await ReconnectAsync(Config!, ct);
            }
        }
    }

    /// <summary>
    /// Processes a received control message from gRPC stream.
    /// </summary>
    private async Task ProcessReceivedMessageAsync(ControlMessage message, CancellationToken ct)
    {
        try
        {
            if (message.Type == "manifest")
            {
                var json = Encoding.UTF8.GetString(message.Payload.ToByteArray());
                var manifest = JsonSerializer.Deserialize<IntentManifest>(json);

                if (manifest != null && _manifestChannel != null)
                {
                    await _manifestChannel.Writer.WriteAsync(manifest, ct);
                    _logger.LogDebug("Received manifest {ManifestId}", manifest.ManifestId);
                }
            }
            else if (message.Type == "ack")
            {
                _logger.LogDebug("Received acknowledgment message");
            }
            else
            {
                _logger.LogDebug("Received unknown message type: {Type}", message.Type);
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
        _logger.LogInformation("Initiating gRPC reconnection");

        try
        {
            if (_streamCall != null)
            {
                await _streamCall.RequestStream.CompleteAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error completing request stream during reconnection");
        }

        _streamCall?.Dispose();
        _streamCall = null;

        await ConnectWithRetryAsync(config, ct);
    }

    /// <inheritdoc />
    protected override async Task TransmitManifestAsync(IntentManifest manifest, CancellationToken ct)
    {
        if (_streamCall == null)
        {
            throw new InvalidOperationException("gRPC stream is not connected");
        }

        var json = JsonSerializer.Serialize(manifest);
        var payload = Google.Protobuf.ByteString.CopyFrom(Encoding.UTF8.GetBytes(json));

        var message = new ControlMessage
        {
            Type = "manifest",
            Payload = payload
        };

        await _sendLock.WaitAsync(ct);
        try
        {
            await _streamCall.RequestStream.WriteAsync(message, ct);
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
        if (_streamCall == null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(heartbeat);
        var payload = Google.Protobuf.ByteString.CopyFrom(Encoding.UTF8.GetBytes(json));

        var message = new ControlMessage
        {
            Type = "heartbeat",
            Payload = payload
        };

        await _sendLock.WaitAsync(ct);
        try
        {
            await _streamCall.RequestStream.WriteAsync(message, ct);
            _logger.LogTrace("Transmitted heartbeat for client {ClientId}", heartbeat.ClientId);
        }
        catch (Exception ex)
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
        if (_streamCall == null)
        {
            throw new InvalidOperationException("gRPC stream is not connected");
        }

        var subscribeJson = JsonSerializer.Serialize(new { action = "subscribe", channelId });
        var payload = Google.Protobuf.ByteString.CopyFrom(Encoding.UTF8.GetBytes(subscribeJson));

        var message = new ControlMessage
        {
            Type = "control",
            Payload = payload
        };

        await _sendLock.WaitAsync(ct);
        try
        {
            await _streamCall.RequestStream.WriteAsync(message, ct);
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
        if (_streamCall == null)
        {
            throw new InvalidOperationException("gRPC stream is not connected");
        }

        var unsubscribeJson = JsonSerializer.Serialize(new { action = "unsubscribe", channelId });
        var payload = Google.Protobuf.ByteString.CopyFrom(Encoding.UTF8.GetBytes(unsubscribeJson));

        var message = new ControlMessage
        {
            Type = "control",
            Payload = payload
        };

        await _sendLock.WaitAsync(ct);
        try
        {
            await _streamCall.RequestStream.WriteAsync(message, ct);
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
        _logger.LogInformation("Closing gRPC control plane connection");

        _connectionCts?.Cancel();

        if (_heartbeatTask != null)
        {
            try { await _heartbeatTask; } catch { }
        }

        if (_receiveTask != null)
        {
            try { await _receiveTask; } catch { }
        }

        if (_streamCall != null)
        {
            try
            {
                await _streamCall.RequestStream.CompleteAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error completing request stream");
            }

            _streamCall.Dispose();
            _streamCall = null;
        }

        _channel?.Dispose();
        _channel = null;

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
            _streamCall?.Dispose();
            _channel?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Generic control message for gRPC streaming (JSON-over-gRPC wrapper).
/// </summary>
public class ControlMessage
{
    /// <summary>Message type (e.g., "manifest", "heartbeat", "control", "ack").</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>JSON payload as byte string.</summary>
    public Google.Protobuf.ByteString Payload { get; set; } = Google.Protobuf.ByteString.Empty;
}

/// <summary>
/// Static gRPC service definition for AEDS control plane (no protobuf compilation required).
/// </summary>
public static class AedsControlPlane
{
    private static readonly Grpc.Core.Method<ControlMessage, ControlMessage> _streamManifestsMethod =
        new Grpc.Core.Method<ControlMessage, ControlMessage>(
            type: Grpc.Core.MethodType.DuplexStreaming,
            serviceName: "AedsControlPlane",
            name: "StreamManifests",
            requestMarshaller: Grpc.Core.Marshallers.Create(
                serializer: msg => SerializeControlMessage(msg),
                deserializer: bytes => DeserializeControlMessage(bytes)),
            responseMarshaller: Grpc.Core.Marshallers.Create(
                serializer: msg => SerializeControlMessage(msg),
                deserializer: bytes => DeserializeControlMessage(bytes))
        );

    /// <summary>
    /// Serializes a ControlMessage to byte array.
    /// </summary>
    private static byte[] SerializeControlMessage(ControlMessage message)
    {
        var json = JsonSerializer.Serialize(new
        {
            type = message.Type,
            payload = Convert.ToBase64String(message.Payload.ToByteArray())
        });
        return Encoding.UTF8.GetBytes(json);
    }

    /// <summary>
    /// Deserializes a ControlMessage from byte array.
    /// </summary>
    private static ControlMessage DeserializeControlMessage(byte[] bytes)
    {
        var json = Encoding.UTF8.GetString(bytes);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var type = root.GetProperty("type").GetString() ?? string.Empty;
        var payloadBase64 = root.GetProperty("payload").GetString() ?? string.Empty;
        var payload = Google.Protobuf.ByteString.CopyFrom(Convert.FromBase64String(payloadBase64));

        return new ControlMessage
        {
            Type = type,
            Payload = payload
        };
    }

    /// <summary>
    /// gRPC client for AEDS control plane.
    /// </summary>
    public class AedsControlPlaneClient
    {
        private readonly Grpc.Core.CallInvoker _callInvoker;

        /// <summary>
        /// Initializes a new instance of the <see cref="AedsControlPlaneClient"/> class.
        /// </summary>
        public AedsControlPlaneClient(Grpc.Core.CallInvoker callInvoker)
        {
            _callInvoker = callInvoker;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AedsControlPlaneClient"/> class.
        /// </summary>
        public AedsControlPlaneClient(GrpcChannel channel)
            : this(channel.CreateCallInvoker())
        {
        }

        /// <summary>
        /// Establishes bidirectional streaming RPC for manifest distribution.
        /// </summary>
        public AsyncDuplexStreamingCall<ControlMessage, ControlMessage> StreamManifests(
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            return _callInvoker.AsyncDuplexStreamingCall(
                _streamManifestsMethod,
                host: null,
                options: new Grpc.Core.CallOptions(
                    headers: headers,
                    deadline: deadline,
                    cancellationToken: cancellationToken));
        }
    }
}
