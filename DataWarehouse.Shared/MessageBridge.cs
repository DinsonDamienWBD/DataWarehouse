using DataWarehouse.SDK.Hosting;
using DataWarehouse.Shared.Models;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;

namespace DataWarehouse.Shared;

/// <summary>
/// Bridge for sending messages to connected DataWarehouse instances
/// Abstracts away connection details for both local and remote connections
/// </summary>
public class MessageBridge
{
    private ConnectionTarget? _currentTarget;
    private TcpClient? _tcpClient;
    private NetworkStream? _networkStream;
    private bool _isConnected;

    public bool IsConnected => _isConnected;
    public ConnectionTarget? CurrentTarget => _currentTarget;

    /// <summary>
    /// Connects to a DataWarehouse instance
    /// </summary>
    /// <param name="target">Connection target</param>
    /// <returns>True if connection was successful</returns>
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
                    // For in-process, we'll just mark as connected
                    // Real implementation would use in-memory queues or direct method calls
                    _isConnected = true;
                    return true;

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
    /// Disconnects from the current instance
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

        _isConnected = false;
        await Task.CompletedTask;
    }

    /// <summary>
    /// Sends a message to the connected instance and waits for response
    /// </summary>
    /// <param name="message">Message to send</param>
    /// <returns>Response message or null if failed</returns>
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
    /// Sends a message without waiting for response (fire and forget)
    /// </summary>
    /// <param name="message">Message to send</param>
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

    private async Task<Message?> SendTcpAsync(Message message)
    {
        if (_networkStream == null || !_isConnected)
            return null;

        try
        {
            // Serialize message to JSON
            var json = JsonConvert.SerializeObject(message);
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
            var response = JsonConvert.DeserializeObject<Message>(responseJson);

            return response;
        }
        catch
        {
            return null;
        }
    }

    private async Task<Message?> SendInProcessAsync(Message message)
    {
        // In-process messaging - this is a placeholder for direct kernel invocation
        // In a real implementation, this would use an in-memory message queue
        // or directly invoke the kernel's message handler

        await Task.CompletedTask;

        // For now, return a mock success response
        return new Message
        {
            Id = Guid.NewGuid().ToString(),
            Type = MessageType.Response,
            CorrelationId = message.Id,
            Command = message.Command,
            Data = new Dictionary<string, object>
            {
                ["success"] = true,
                ["message"] = "In-process command execution (placeholder)"
            }
        };
    }

    /// <summary>
    /// Pings the instance to check if connection is still alive
    /// </summary>
    /// <returns>True if instance is responsive</returns>
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
    /// Gets connection statistics
    /// </summary>
    /// <returns>Dictionary of connection stats</returns>
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
}
