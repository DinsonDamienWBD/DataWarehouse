using DataWarehouse.SDK.Hosting;
using DataWarehouse.Shared.Models;
using System.Text.Json;

namespace DataWarehouse.Shared;

/// <summary>
/// Connection profile for saving/loading connections
/// </summary>
public class ConnectionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public ConnectionTarget Target { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastConnectedAt { get; set; }
    public bool IsDefault { get; set; }
}

/// <summary>
/// Manages connections to DataWarehouse instances
/// </summary>
public class InstanceManager
{
    private readonly CapabilityManager _capabilityManager;
    private readonly MessageBridge _messageBridge;
    private ConnectionTarget? _currentConnection;
    private bool _isConnected;

    public event EventHandler<ConnectionTarget>? ConnectionChanged;
    public event EventHandler<bool>? ConnectionStatusChanged;

    public ConnectionTarget? CurrentConnection => _currentConnection;
    public bool IsConnected => _isConnected;
    public InstanceCapabilities? Capabilities => _capabilityManager.Capabilities;

    public InstanceManager()
    {
        _capabilityManager = new CapabilityManager();
        _messageBridge = new MessageBridge();
    }

    public InstanceManager(CapabilityManager capabilityManager, MessageBridge messageBridge)
    {
        _capabilityManager = capabilityManager;
        _messageBridge = messageBridge;
    }

    /// <summary>
    /// Connects to a DataWarehouse instance
    /// </summary>
    /// <param name="target">Connection target information</param>
    /// <returns>True if connection was successful</returns>
    public async Task<bool> ConnectAsync(ConnectionTarget target)
    {
        try
        {
            // Connect through message bridge
            var connected = await _messageBridge.ConnectAsync(target);
            if (!connected)
                return false;

            // Query capabilities from instance
            var capabilitiesResponse = await _messageBridge.SendAsync(new Message
            {
                Id = Guid.NewGuid().ToString(),
                Type = MessageType.Request,
                Command = "system.capabilities",
                Data = new Dictionary<string, object>()
            });

            if (capabilitiesResponse?.Data != null &&
                capabilitiesResponse.Data.ContainsKey("capabilities"))
            {
                var capabilitiesJson = JsonSerializer.Serialize(capabilitiesResponse.Data["capabilities"]);
                var capabilities = JsonSerializer.Deserialize<InstanceCapabilities>(capabilitiesJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (capabilities != null)
                {
                    _capabilityManager.UpdateCapabilities(capabilities);
                }
            }

            _currentConnection = target;
            _isConnected = true;

            ConnectionChanged?.Invoke(this, target);
            ConnectionStatusChanged?.Invoke(this, true);

            return true;
        }
        catch
        {
            _isConnected = false;
            ConnectionStatusChanged?.Invoke(this, false);
            return false;
        }
    }

    /// <summary>
    /// Disconnects from the current instance
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_isConnected)
        {
            await _messageBridge.DisconnectAsync();
            _isConnected = false;
            ConnectionStatusChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// Executes a command on the connected instance
    /// </summary>
    /// <param name="command">Command to execute</param>
    /// <param name="parameters">Command parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Command result</returns>
    public async Task<Message?> ExecuteAsync(
        string command,
        Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (!_isConnected)
        {
            throw new InvalidOperationException(
                "Not connected to a DataWarehouse instance. Use 'dw connect' to connect to an instance first.");
        }

        var message = new Message
        {
            Id = Guid.NewGuid().ToString(),
            Type = MessageType.Request,
            Command = command,
            Data = parameters ?? new Dictionary<string, object>()
        };

        cancellationToken.ThrowIfCancellationRequested();
        return await _messageBridge.SendAsync(message);
    }

    /// <summary>
    /// Connects to a remote instance.
    /// </summary>
    /// <param name="host">Remote host address.</param>
    /// <param name="port">Remote port.</param>
    /// <returns>True if connection was successful.</returns>
    public Task<bool> ConnectRemoteAsync(string host, int port)
    {
        return ConnectAsync(new ConnectionTarget
        {
            Name = $"Remote ({host}:{port})",
            Type = ConnectionType.Remote,
            Host = host,
            Port = port
        });
    }

    /// <summary>
    /// Connects to a local instance by path.
    /// </summary>
    /// <param name="path">Path to local instance.</param>
    /// <returns>True if connection was successful.</returns>
    public Task<bool> ConnectLocalAsync(string path)
    {
        return ConnectAsync(new ConnectionTarget
        {
            Name = $"Local ({path})",
            Type = ConnectionType.Local,
            Host = path,
            Port = 0
        });
    }

    /// <summary>
    /// Connects to an in-process instance.
    /// </summary>
    /// <returns>True if connection was successful.</returns>
    public Task<bool> ConnectInProcessAsync()
    {
        return ConnectAsync(new ConnectionTarget
        {
            Name = "In-Process",
            Type = ConnectionType.InProcess,
            Host = "localhost",
            Port = 0
        });
    }

    /// <summary>
    /// Saves a connection profile
    /// </summary>
    /// <param name="profile">Profile to save</param>
    public void SaveProfile(ConnectionProfile profile)
    {
        var profilesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DataWarehouse",
            "Profiles");

        Directory.CreateDirectory(profilesDir);

        var profilePath = Path.Combine(profilesDir, $"{profile.Id}.json");
        var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(profilePath, json);
    }

    /// <summary>
    /// Loads all saved connection profiles
    /// </summary>
    /// <returns>List of saved profiles</returns>
    public List<ConnectionProfile> LoadProfiles()
    {
        var profilesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DataWarehouse",
            "Profiles");

        if (!Directory.Exists(profilesDir))
            return new List<ConnectionProfile>();

        var profiles = new List<ConnectionProfile>();
        foreach (var file in Directory.GetFiles(profilesDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var profile = JsonSerializer.Deserialize<ConnectionProfile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (profile != null)
                    profiles.Add(profile);
            }
            catch
            {
                // Skip invalid profiles
            }
        }

        return profiles;
    }

    /// <summary>
    /// Deletes a saved connection profile
    /// </summary>
    /// <param name="profileId">ID of profile to delete</param>
    public void DeleteProfile(string profileId)
    {
        var profilesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DataWarehouse",
            "Profiles");

        var profilePath = Path.Combine(profilesDir, $"{profileId}.json");
        if (File.Exists(profilePath))
            File.Delete(profilePath);
    }

    /// <summary>
    /// Gets the default connection profile
    /// </summary>
    /// <returns>Default profile or null if none is set</returns>
    public ConnectionProfile? GetDefaultProfile()
    {
        var profiles = LoadProfiles();
        return profiles.FirstOrDefault(p => p.IsDefault);
    }

    /// <summary>
    /// Sets a profile as the default
    /// </summary>
    /// <param name="profileId">ID of profile to set as default</param>
    public void SetDefaultProfile(string profileId)
    {
        var profiles = LoadProfiles();

        foreach (var profile in profiles)
        {
            profile.IsDefault = profile.Id == profileId;
            SaveProfile(profile);
        }
    }

    /// <summary>
    /// Switches to a different instance
    /// </summary>
    /// <param name="target">New connection target</param>
    public async Task<bool> SwitchInstanceAsync(ConnectionTarget target)
    {
        await DisconnectAsync();
        return await ConnectAsync(target);
    }

    /// <summary>
    /// Executes a natural language command using AI-powered interpretation.
    /// </summary>
    /// <param name="query">Natural language query to interpret and execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Command result.</returns>
    public async Task<Message?> ExecuteNaturalLanguageAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        // Route through the NLP command interpreter
        return await ExecuteAsync("nlp.interpret", new Dictionary<string, object>
        {
            ["query"] = query,
            ["context"] = "command-palette"
        }, cancellationToken);
    }
}
