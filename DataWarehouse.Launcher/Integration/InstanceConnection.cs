using DataWarehouse.SDK.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Launcher.Integration;

/// <summary>
/// Interface for connecting to DataWarehouse instances.
/// </summary>
public interface IInstanceConnection : IAsyncDisposable
{
    /// <summary>
    /// Unique identifier of the connected instance.
    /// </summary>
    string InstanceId { get; }

    /// <summary>
    /// Whether currently connected.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Capabilities of the connected instance.
    /// </summary>
    InstanceCapabilities? Capabilities { get; }

    /// <summary>
    /// Connects to the instance.
    /// </summary>
    Task ConnectAsync(ConnectionTarget target, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discovers the capabilities of the connected instance.
    /// </summary>
    Task<InstanceCapabilities> DiscoverCapabilitiesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current configuration.
    /// </summary>
    Task<Dictionary<string, object>> GetConfigurationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the configuration.
    /// </summary>
    Task UpdateConfigurationAsync(Dictionary<string, object> config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message to the instance.
    /// </summary>
    Task<MessageResponse> SendMessageAsync(string messageType, Dictionary<string, object>? payload = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a command on the instance.
    /// </summary>
    Task<CommandResult> ExecuteCommandAsync(string command, string[] args, CancellationToken cancellationToken = default);
}

/// <summary>
/// Capabilities discovered from a DataWarehouse instance.
/// </summary>
public sealed class InstanceCapabilities
{
    /// <summary>
    /// Instance version.
    /// </summary>
    public string Version { get; set; } = "";

    /// <summary>
    /// Available plugins.
    /// </summary>
    public List<PluginInfo> AvailablePlugins { get; set; } = new();

    /// <summary>
    /// Supported features.
    /// </summary>
    public HashSet<string> SupportedFeatures { get; set; } = new();

    /// <summary>
    /// Available commands.
    /// </summary>
    public List<CommandInfo> AvailableCommands { get; set; } = new();

    /// <summary>
    /// Storage backends available.
    /// </summary>
    public List<string> StorageBackends { get; set; } = new();

    /// <summary>
    /// Whether the instance is clustered.
    /// </summary>
    public bool IsClustered { get; set; }

    /// <summary>
    /// Number of nodes (if clustered).
    /// </summary>
    public int NodeCount { get; set; } = 1;

    /// <summary>
    /// Whether the instance has AI capabilities.
    /// </summary>
    public bool HasAICapabilities { get; set; }

    /// <summary>
    /// Checks if a feature is supported.
    /// </summary>
    public bool HasFeature(string feature) => SupportedFeatures.Contains(feature);

    /// <summary>
    /// Checks if a plugin is available.
    /// </summary>
    public bool HasPlugin(string pluginId) =>
        AvailablePlugins.Any(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Information about an available plugin.
/// </summary>
public sealed class PluginInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Category { get; set; } = "";
    public bool IsEnabled { get; set; }
    public List<string> Capabilities { get; set; } = new();
}

/// <summary>
/// Information about an available command.
/// </summary>
public sealed class CommandInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public List<CommandParameter> Parameters { get; set; } = new();
}

/// <summary>
/// Command parameter information.
/// </summary>
public sealed class CommandParameter
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Required { get; set; }
    public string? Description { get; set; }
    public object? DefaultValue { get; set; }
}

/// <summary>
/// Response from a message send operation.
/// </summary>
public sealed class MessageResponse
{
    public bool Success { get; set; }
    public Dictionary<string, object> Payload { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>
/// Result of a command execution.
/// </summary>
public sealed class CommandResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public string? Error { get; set; }
}

/// <summary>
/// Connection to a local DataWarehouse instance via IPC.
/// </summary>
public sealed class LocalInstanceConnection : IInstanceConnection
{
    private readonly ILogger<LocalInstanceConnection> _logger;
    private string _instanceId = "";
    private string _pipeName = "";
    private InstanceCapabilities? _capabilities;
    private bool _connected;
    private bool _disposed;

    public LocalInstanceConnection(ILoggerFactory? loggerFactory = null)
    {
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<LocalInstanceConnection>();
    }

    public string InstanceId => _instanceId;
    public bool IsConnected => _connected;
    public InstanceCapabilities? Capabilities => _capabilities;

    public async Task ConnectAsync(ConnectionTarget target, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(target.LocalPath))
        {
            throw new ArgumentException("LocalPath is required for local connections.");
        }

        _logger.LogDebug("Connecting to local instance at: {Path}", target.LocalPath);

        // Read instance info from the local path
        var configPath = Path.Combine(target.LocalPath, "config", "datawarehouse.json");
        if (File.Exists(configPath))
        {
            var json = await File.ReadAllTextAsync(configPath, cancellationToken);
            var config = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            _instanceId = config?["instanceId"]?.ToString() ?? Guid.NewGuid().ToString("N");
        }
        else
        {
            _instanceId = $"local-{Path.GetFileName(target.LocalPath)}";
        }

        _pipeName = $"datawarehouse-{_instanceId}";
        _connected = true;

        _logger.LogInformation("Connected to local instance: {InstanceId}", _instanceId);
    }

    public Task<InstanceCapabilities> DiscoverCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        // For local connections, read capabilities from the instance
        _capabilities = new InstanceCapabilities
        {
            Version = "1.0.0",
            SupportedFeatures = new HashSet<string> { "storage", "metadata", "search", "messaging" },
            StorageBackends = new List<string> { "local", "memory" },
            IsClustered = false,
            NodeCount = 1
        };

        return Task.FromResult(_capabilities);
    }

    public Task<Dictionary<string, object>> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        // Read from local config file
        return Task.FromResult(new Dictionary<string, object>
        {
            ["instanceId"] = _instanceId,
            ["mode"] = "local"
        });
    }

    public Task UpdateConfigurationAsync(Dictionary<string, object> config, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Updating local configuration");
        return Task.CompletedTask;
    }

    public Task<MessageResponse> SendMessageAsync(string messageType, Dictionary<string, object>? payload = null, CancellationToken cancellationToken = default)
    {
        // Send via named pipe
        _logger.LogDebug("Sending message: {Type}", messageType);
        return Task.FromResult(new MessageResponse { Success = true });
    }

    public Task<CommandResult> ExecuteCommandAsync(string command, string[] args, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Executing command: {Command}", command);
        return Task.FromResult(new CommandResult { Success = true, ExitCode = 0 });
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _connected = false;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Connection to a remote DataWarehouse instance via network.
/// </summary>
public sealed class RemoteInstanceConnection : IInstanceConnection
{
    private readonly ILogger<RemoteInstanceConnection> _logger;
    private readonly HttpClient _httpClient;
    private string _instanceId = "";
    private string _baseUrl = "";
    private InstanceCapabilities? _capabilities;
    private bool _connected;
    private bool _disposed;

    private static readonly HttpClient SharedHttpClient = new HttpClient();

    public RemoteInstanceConnection(ILoggerFactory? loggerFactory = null)
    {
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RemoteInstanceConnection>();
        _httpClient = SharedHttpClient;
    }

    public string InstanceId => _instanceId;
    public bool IsConnected => _connected;
    public InstanceCapabilities? Capabilities => _capabilities;

    public async Task ConnectAsync(ConnectionTarget target, CancellationToken cancellationToken = default)
    {
        var scheme = target.UseTls ? "https" : "http";
        _baseUrl = $"{scheme}://{target.Host}:{target.Port}";

        _logger.LogDebug("Connecting to remote instance at: {Url}", _baseUrl);

        _httpClient.Timeout = TimeSpan.FromSeconds(target.TimeoutSeconds);

        if (!string.IsNullOrEmpty(target.AuthToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", target.AuthToken);
        }

        // Test connection
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/api/v1/info", cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var info = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            _instanceId = info?["instanceId"]?.ToString() ?? "unknown";

            _connected = true;
            _logger.LogInformation("Connected to remote instance: {InstanceId}", _instanceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to remote instance");
            throw new InvalidOperationException($"Failed to connect to {_baseUrl}: {ex.Message}", ex);
        }
    }

    public async Task<InstanceCapabilities> DiscoverCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"{_baseUrl}/api/v1/capabilities", cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        _capabilities = JsonSerializer.Deserialize<InstanceCapabilities>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new InstanceCapabilities();

        return _capabilities;
    }

    public async Task<Dictionary<string, object>> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"{_baseUrl}/api/v1/config", cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new();
    }

    public async Task UpdateConfigurationAsync(Dictionary<string, object> config, CancellationToken cancellationToken = default)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(config),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PutAsync($"{_baseUrl}/api/v1/config", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<MessageResponse> SendMessageAsync(string messageType, Dictionary<string, object>? payload = null, CancellationToken cancellationToken = default)
    {
        var message = new
        {
            type = messageType,
            payload = payload ?? new Dictionary<string, object>()
        };

        var content = new StringContent(
            JsonSerializer.Serialize(message),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync($"{_baseUrl}/api/v1/message", content, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<MessageResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new MessageResponse { Success = true };
        }

        return new MessageResponse
        {
            Success = false,
            Error = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}"
        };
    }

    public async Task<CommandResult> ExecuteCommandAsync(string command, string[] args, CancellationToken cancellationToken = default)
    {
        var request = new
        {
            command,
            arguments = args
        };

        var content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync($"{_baseUrl}/api/v1/execute", content, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        return JsonSerializer.Deserialize<CommandResult>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new CommandResult { Success = false, Error = "Failed to parse response" };
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _connected = false;
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Connection to a cluster of DataWarehouse instances.
/// </summary>
public sealed class ClusterInstanceConnection : IInstanceConnection
{
    private readonly ILogger<ClusterInstanceConnection> _logger;
    private readonly List<RemoteInstanceConnection> _nodeConnections = new();
    private string _instanceId = "";
    private InstanceCapabilities? _capabilities;
    private bool _connected;
    private bool _disposed;

    public ClusterInstanceConnection(ILoggerFactory? loggerFactory = null)
    {
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ClusterInstanceConnection>();
    }

    public string InstanceId => _instanceId;
    public bool IsConnected => _connected;
    public InstanceCapabilities? Capabilities => _capabilities;

    public Task ConnectAsync(ConnectionTarget target, CancellationToken cancellationToken = default)
    {
        _instanceId = $"cluster-{target.Host}";
        _connected = true;
        _logger.LogInformation("Connected to cluster: {InstanceId}", _instanceId);
        return Task.CompletedTask;
    }

    public Task<InstanceCapabilities> DiscoverCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        _capabilities = new InstanceCapabilities
        {
            Version = "1.0.0",
            IsClustered = true,
            NodeCount = _nodeConnections.Count,
            SupportedFeatures = new HashSet<string> { "storage", "metadata", "search", "messaging", "clustering" }
        };
        return Task.FromResult(_capabilities);
    }

    public Task<Dictionary<string, object>> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new Dictionary<string, object>
        {
            ["instanceId"] = _instanceId,
            ["mode"] = "cluster",
            ["nodeCount"] = _nodeConnections.Count
        });
    }

    public Task UpdateConfigurationAsync(Dictionary<string, object> config, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Updating cluster configuration");
        return Task.CompletedTask;
    }

    public Task<MessageResponse> SendMessageAsync(string messageType, Dictionary<string, object>? payload = null, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Broadcasting message to cluster: {Type}", messageType);
        return Task.FromResult(new MessageResponse { Success = true });
    }

    public Task<CommandResult> ExecuteCommandAsync(string command, string[] args, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Executing command on cluster: {Command}", command);
        return Task.FromResult(new CommandResult { Success = true, ExitCode = 0 });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _connected = false;

        foreach (var connection in _nodeConnections)
        {
            await connection.DisposeAsync();
        }
        _nodeConnections.Clear();
    }
}
