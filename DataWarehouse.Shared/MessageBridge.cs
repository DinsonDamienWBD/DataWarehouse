using DataWarehouse.SDK.Hosting;
using DataWarehouse.Shared.Models;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace DataWarehouse.Shared;

/// <summary>
/// Bridge for sending messages to connected DataWarehouse instances.
/// Abstracts away connection details for both local, remote, and in-process connections.
/// In-process mode uses a Channel&lt;T&gt;-based producer-consumer queue for real message dispatch.
/// </summary>
public class MessageBridge
{
    private ConnectionTarget? _currentTarget;
    private TcpClient? _tcpClient;
    private NetworkStream? _networkStream;
    private bool _isConnected;

    // In-process Channel-based messaging fields
    private Channel<(Message request, TaskCompletionSource<Message?> response)>? _inProcessChannel;
    private CancellationTokenSource? _inProcessCts;
    private Task? _inProcessConsumerTask;
    private Func<Message, CancellationToken, Task<Message?>>? _inProcessHandler;

    // Topic subscription support for event-driven communication
    private readonly ConcurrentDictionary<string, List<Func<Message, Task>>> _topicSubscriptions = new();

    /// <summary>
    /// Gets whether the bridge is currently connected to an instance.
    /// </summary>
    public bool IsConnected => _isConnected;

    /// <summary>
    /// Gets the current connection target.
    /// </summary>
    public ConnectionTarget? CurrentTarget => _currentTarget;

    /// <summary>
    /// Configures the in-process message handler that processes messages when in in-process mode.
    /// This handler is set by the DataWarehouseHost/kernel when starting embedded mode.
    /// </summary>
    /// <param name="handler">A function that receives a Message and returns a response Message.</param>
    /// <exception cref="ArgumentNullException">Thrown when handler is null.</exception>
    public void ConfigureInProcessHandler(Func<Message, CancellationToken, Task<Message?>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _inProcessHandler = handler;
    }

    /// <summary>
    /// Subscribes to messages matching a specific topic (Command field).
    /// Used by DynamicCommandRegistry to subscribe to capability events such as
    /// capability.changed, plugin.loaded, and plugin.unloaded.
    /// </summary>
    /// <param name="topic">The topic/command pattern to subscribe to.</param>
    /// <param name="handler">The async handler to invoke when a matching message arrives.</param>
    /// <exception cref="ArgumentNullException">Thrown when topic or handler is null.</exception>
    public void SubscribeToTopic(string topic, Func<Message, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(handler);

        _topicSubscriptions.AddOrUpdate(
            topic,
            _ => new List<Func<Message, Task>> { handler },
            (_, existing) =>
            {
                lock (existing)
                {
                    existing.Add(handler);
                }
                return existing;
            });
    }

    /// <summary>
    /// Connects to a DataWarehouse instance.
    /// </summary>
    /// <param name="target">Connection target.</param>
    /// <returns>True if connection was successful.</returns>
    public async Task<bool> ConnectAsync(ConnectionTarget target)
    {
        try
        {
            _currentTarget = target;

            switch (target.Type)
            {
                case ConnectionType.Local:
                case ConnectionType.Remote:
                    return await ConnectTcpAsync(target.Host, target.Port);

                case ConnectionType.InProcess:
                    return ConnectInProcess();

                default:
                    return false;
            }
        }
        catch
        {
            _isConnected = false;
            return false;
        }
    }

    /// <summary>
    /// Disconnects from the current instance and cleans up resources.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_tcpClient != null)
        {
            _networkStream?.Close();
            _tcpClient.Close();
            _tcpClient = null;
            _networkStream = null;
        }

        // Clean up in-process channel resources
        if (_inProcessChannel != null)
        {
            _inProcessCts?.Cancel();
            _inProcessChannel.Writer.TryComplete();

            if (_inProcessConsumerTask != null)
            {
                try
                {
                    await _inProcessConsumerTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    // Consumer did not exit in time; proceed with cleanup
                }
                catch (OperationCanceledException)
                {
                    // Expected on cancellation
                }
            }

            _inProcessCts?.Dispose();
            _inProcessCts = null;
            _inProcessChannel = null;
            _inProcessConsumerTask = null;
        }

        _isConnected = false;
    }

    /// <summary>
    /// Sends a message to the connected instance and waits for response.
    /// </summary>
    /// <param name="message">Message to send.</param>
    /// <returns>Response message or null if failed.</returns>
    /// <exception cref="InvalidOperationException">Thrown when not connected to an instance.</exception>
    public async Task<Message?> SendAsync(Message message)
    {
        if (!_isConnected || _currentTarget == null)
            throw new InvalidOperationException("Not connected to an instance");

        switch (_currentTarget.Type)
        {
            case ConnectionType.Local:
            case ConnectionType.Remote:
                return await SendTcpAsync(message);

            case ConnectionType.InProcess:
                return await SendInProcessAsync(message);

            default:
                return null;
        }
    }

    /// <summary>
    /// Sends a message without waiting for response (fire and forget).
    /// </summary>
    /// <param name="message">Message to send.</param>
    /// <exception cref="InvalidOperationException">Thrown when not connected to an instance.</exception>
    public async Task SendOneWayAsync(Message message)
    {
        if (!_isConnected || _currentTarget == null)
            throw new InvalidOperationException("Not connected to an instance");

        message.Type = MessageType.Event;

        switch (_currentTarget.Type)
        {
            case ConnectionType.Local:
            case ConnectionType.Remote:
                await SendTcpAsync(message);
                break;

            case ConnectionType.InProcess:
                await SendInProcessAsync(message);
                break;
        }
    }

    /// <summary>
    /// Pings the instance to check if connection is still alive.
    /// </summary>
    /// <returns>True if instance is responsive.</returns>
    public async Task<bool> PingAsync()
    {
        if (!_isConnected)
            return false;

        try
        {
            var response = await SendAsync(new Message
            {
                Id = Guid.NewGuid().ToString(),
                Type = MessageType.Request,
                Command = "system.ping",
                Data = new Dictionary<string, object>()
            });

            return response != null && response.Type == MessageType.Response;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets connection statistics.
    /// </summary>
    /// <returns>Dictionary of connection stats.</returns>
    public Dictionary<string, object> GetConnectionStats()
    {
        var stats = new Dictionary<string, object>
        {
            ["isConnected"] = _isConnected,
            ["connectionType"] = _currentTarget?.Type.ToString() ?? "None"
        };

        if (_currentTarget != null)
        {
            stats["host"] = _currentTarget.Host;
            stats["port"] = _currentTarget.Port;
            stats["name"] = _currentTarget.Name;
        }

        return stats;
    }

    #region Private Methods

    private async Task<bool> ConnectTcpAsync(string address, int port)
    {
        try
        {
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(address, port);
            _networkStream = _tcpClient.GetStream();
            _isConnected = true;
            return true;
        }
        catch
        {
            _isConnected = false;
            return false;
        }
    }

    /// <summary>
    /// Initializes the in-process Channel-based messaging infrastructure.
    /// </summary>
    private bool ConnectInProcess()
    {
        _inProcessChannel = Channel.CreateBounded<(Message request, TaskCompletionSource<Message?> response)>(
            new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

        _inProcessCts = new CancellationTokenSource();
        _inProcessConsumerTask = Task.Run(() => RunInProcessConsumerAsync(_inProcessCts.Token));
        _isConnected = true;
        return true;
    }

    /// <summary>
    /// Consumer loop that reads from the in-process channel and invokes the handler.
    /// </summary>
    private async Task RunInProcessConsumerAsync(CancellationToken ct)
    {
        if (_inProcessChannel == null) return;

        try
        {
            await foreach (var (request, tcs) in _inProcessChannel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    // Dispatch topic subscriptions for events
                    await DispatchTopicSubscriptionsAsync(request);

                    if (_inProcessHandler != null)
                    {
                        var response = await _inProcessHandler(request, ct);
                        tcs.SetResult(response);
                    }
                    else
                    {
                        // No handler configured -- return a response indicating no handler
                        tcs.SetResult(new Message
                        {
                            Id = Guid.NewGuid().ToString(),
                            Type = MessageType.Response,
                            CorrelationId = request.Id,
                            Command = request.Command,
                            Data = new Dictionary<string, object>
                            {
                                ["success"] = false,
                                ["message"] = "No in-process handler configured. Call ConfigureInProcessHandler first."
                            }
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetCanceled(ct);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    /// <summary>
    /// Dispatches a message to all topic subscribers matching the message's Command field.
    /// </summary>
    private async Task DispatchTopicSubscriptionsAsync(Message message)
    {
        if (_topicSubscriptions.TryGetValue(message.Command, out var handlers))
        {
            List<Func<Message, Task>> handlersCopy;
            lock (handlers)
            {
                handlersCopy = new List<Func<Message, Task>>(handlers);
            }

            foreach (var handler in handlersCopy)
            {
                try
                {
                    await handler(message);
                }
                catch
                {
                    // Swallow subscriber errors to avoid breaking the message pipeline
                }
            }
        }
    }

    private async Task<Message?> SendTcpAsync(Message message)
    {
        if (_networkStream == null || !_isConnected)
            return null;

        try
        {
            // Serialize message to JSON
            var json = JsonSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);

            // Send message length prefix (4 bytes)
            var lengthBytes = BitConverter.GetBytes(bytes.Length);
            await _networkStream.WriteAsync(lengthBytes, 0, lengthBytes.Length);

            // Send message body
            await _networkStream.WriteAsync(bytes, 0, bytes.Length);
            await _networkStream.FlushAsync();

            // For events, don't wait for response
            if (message.Type == MessageType.Event)
                return null;

            // Read response length prefix
            var responseLengthBytes = new byte[4];
            var bytesRead = await _networkStream.ReadAsync(responseLengthBytes, 0, 4);
            if (bytesRead != 4)
                return null;

            var responseLength = BitConverter.ToInt32(responseLengthBytes, 0);

            // Read response body
            var responseBytes = new byte[responseLength];
            var totalRead = 0;
            while (totalRead < responseLength)
            {
                bytesRead = await _networkStream.ReadAsync(
                    responseBytes, totalRead, responseLength - totalRead);
                if (bytesRead == 0)
                    break;
                totalRead += bytesRead;
            }

            if (totalRead != responseLength)
                return null;

            // Deserialize response
            var responseJson = Encoding.UTF8.GetString(responseBytes);
            var response = JsonSerializer.Deserialize<Message>(responseJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return response;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sends a message through the in-process Channel-based queue and awaits a response.
    /// Uses a TaskCompletionSource to bridge the producer-consumer pattern with async/await.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <returns>The response message, or null if the message was an event.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the in-process channel is not initialized.</exception>
    /// <exception cref="TimeoutException">Thrown when processing exceeds 30 seconds.</exception>
    private async Task<Message?> SendInProcessAsync(Message message)
    {
        if (_inProcessChannel == null)
        {
            throw new InvalidOperationException(
                "In-process channel not initialized. Call ConfigureInProcessHandler first.");
        }

        // Dispatch topic subscriptions for events sent locally
        await DispatchTopicSubscriptionsAsync(message);

        // For fire-and-forget events, write to channel without waiting
        if (message.Type == MessageType.Event)
        {
            var eventTcs = new TaskCompletionSource<Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _inProcessChannel.Writer.WriteAsync((message, eventTcs));
            return null;
        }

        var tcs = new TaskCompletionSource<Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _inProcessChannel.Writer.WriteAsync((message, tcs));

        // Await response with 30-second timeout
        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("In-process message processing timed out after 30 seconds");
        }
    }

    #endregion
}
