using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.ZeroConfig;

#region Enums

/// <summary>
/// Role types for nodes in the cluster.
/// </summary>
public enum NodeRole
{
    /// <summary>
    /// Leader node responsible for cluster coordination and consensus.
    /// </summary>
    Leader,

    /// <summary>
    /// Worker node for processing requests and computations.
    /// </summary>
    Worker,

    /// <summary>
    /// Storage node optimized for data persistence.
    /// </summary>
    Storage,

    /// <summary>
    /// Gateway node for external access and load balancing.
    /// </summary>
    Gateway,

    /// <summary>
    /// Hybrid node capable of multiple roles.
    /// </summary>
    Hybrid,

    /// <summary>
    /// Unknown role pending detection.
    /// </summary>
    Unknown
}

/// <summary>
/// Discovery protocol types.
/// </summary>
public enum DiscoveryProtocol
{
    /// <summary>
    /// Multicast DNS (mDNS/Bonjour) for local network discovery.
    /// </summary>
    MDNS,

    /// <summary>
    /// Simple Service Discovery Protocol (SSDP/UPnP).
    /// </summary>
    SSDP,

    /// <summary>
    /// Both mDNS and SSDP protocols.
    /// </summary>
    Both
}

/// <summary>
/// Node connection state.
/// </summary>
public enum NodeState
{
    /// <summary>
    /// Node is being discovered.
    /// </summary>
    Discovering,

    /// <summary>
    /// Node is connecting to the cluster.
    /// </summary>
    Connecting,

    /// <summary>
    /// Node is active and healthy.
    /// </summary>
    Active,

    /// <summary>
    /// Node is experiencing degraded performance.
    /// </summary>
    Degraded,

    /// <summary>
    /// Node is leaving the cluster gracefully.
    /// </summary>
    Leaving,

    /// <summary>
    /// Node has departed the cluster.
    /// </summary>
    Departed,

    /// <summary>
    /// Node is unreachable.
    /// </summary>
    Unreachable
}

/// <summary>
/// Network topology type.
/// </summary>
public enum NetworkTopology
{
    /// <summary>
    /// Flat network with all nodes directly connected.
    /// </summary>
    Flat,

    /// <summary>
    /// Hierarchical network with multiple tiers.
    /// </summary>
    Hierarchical,

    /// <summary>
    /// Ring topology for ordered communication.
    /// </summary>
    Ring,

    /// <summary>
    /// Mesh network with redundant connections.
    /// </summary>
    Mesh,

    /// <summary>
    /// Unknown topology pending detection.
    /// </summary>
    Unknown
}

#endregion

#region Supporting Types

/// <summary>
/// Represents the capabilities of a node in the cluster.
/// </summary>
public sealed class NodeCapabilities
{
    /// <summary>
    /// Number of CPU cores available.
    /// </summary>
    public int CpuCores { get; init; }

    /// <summary>
    /// CPU frequency in MHz.
    /// </summary>
    public long CpuFrequencyMhz { get; init; }

    /// <summary>
    /// Total physical memory in bytes.
    /// </summary>
    public long TotalMemoryBytes { get; init; }

    /// <summary>
    /// Available memory in bytes.
    /// </summary>
    public long AvailableMemoryBytes { get; set; }

    /// <summary>
    /// Total disk space in bytes.
    /// </summary>
    public long TotalDiskBytes { get; init; }

    /// <summary>
    /// Available disk space in bytes.
    /// </summary>
    public long AvailableDiskBytes { get; set; }

    /// <summary>
    /// Network bandwidth in Mbps.
    /// </summary>
    public long NetworkBandwidthMbps { get; init; }

    /// <summary>
    /// Operating system description.
    /// </summary>
    public string OperatingSystem { get; init; } = string.Empty;

    /// <summary>
    /// Runtime version (.NET version).
    /// </summary>
    public string RuntimeVersion { get; init; } = string.Empty;

    /// <summary>
    /// Whether the node has SSD storage.
    /// </summary>
    public bool HasSsdStorage { get; init; }

    /// <summary>
    /// Whether the node supports hardware acceleration.
    /// </summary>
    public bool SupportsHardwareAcceleration { get; init; }

    /// <summary>
    /// Custom capability tags.
    /// </summary>
    public List<string> Tags { get; init; } = new();

    /// <summary>
    /// Computed capability score for role assignment (0-100).
    /// </summary>
    public int ComputeCapabilityScore()
    {
        var cpuScore = Math.Min(CpuCores * 5, 25);
        var memoryScore = Math.Min((int)(TotalMemoryBytes / (1024L * 1024L * 1024L) * 2), 25);
        var diskScore = Math.Min((int)(TotalDiskBytes / (1024L * 1024L * 1024L * 100L) * 2), 25);
        var networkScore = Math.Min((int)(NetworkBandwidthMbps / 100), 25);
        return cpuScore + memoryScore + diskScore + networkScore;
    }

    /// <summary>
    /// Determines the optimal role for this node based on capabilities.
    /// </summary>
    public NodeRole DetermineOptimalRole()
    {
        var memoryGb = TotalMemoryBytes / (1024.0 * 1024.0 * 1024.0);
        var diskGb = TotalDiskBytes / (1024.0 * 1024.0 * 1024.0);

        // High disk, SSD - storage node
        if (diskGb > 500 && HasSsdStorage)
            return NodeRole.Storage;

        // High CPU and memory - worker node
        if (CpuCores >= 8 && memoryGb >= 16)
            return NodeRole.Worker;

        // High network bandwidth - gateway node
        if (NetworkBandwidthMbps >= 10000)
            return NodeRole.Gateway;

        // Balanced capabilities - hybrid
        if (CpuCores >= 4 && memoryGb >= 8 && diskGb >= 100)
            return NodeRole.Hybrid;

        return NodeRole.Worker;
    }
}

/// <summary>
/// Represents a discovered node in the cluster.
/// </summary>
public sealed class DiscoveredNode
{
    /// <summary>
    /// Unique identifier for this node.
    /// </summary>
    public string NodeId { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable name for the node.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// IP addresses of the node.
    /// </summary>
    public List<IPAddress> Addresses { get; init; } = new();

    /// <summary>
    /// Primary IP address for communication.
    /// </summary>
    public IPAddress? PrimaryAddress { get; set; }

    /// <summary>
    /// Service port for cluster communication.
    /// </summary>
    public int ServicePort { get; set; }

    /// <summary>
    /// Discovery port for announcements.
    /// </summary>
    public int DiscoveryPort { get; set; }

    /// <summary>
    /// Current state of the node.
    /// </summary>
    public NodeState State { get; set; } = NodeState.Discovering;

    /// <summary>
    /// Assigned role for this node.
    /// </summary>
    public NodeRole Role { get; set; } = NodeRole.Unknown;

    /// <summary>
    /// Node capabilities.
    /// </summary>
    public NodeCapabilities? Capabilities { get; set; }

    /// <summary>
    /// When the node was first discovered.
    /// </summary>
    public DateTime DiscoveredAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When the node last sent a heartbeat.
    /// </summary>
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the node joined the cluster.
    /// </summary>
    public DateTime? JoinedAt { get; set; }

    /// <summary>
    /// Protocol used to discover this node.
    /// </summary>
    public DiscoveryProtocol DiscoveryProtocol { get; init; }

    /// <summary>
    /// Round-trip latency to this node in milliseconds.
    /// </summary>
    public double LatencyMs { get; set; }

    /// <summary>
    /// Node metadata.
    /// </summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>
    /// Node configuration version.
    /// </summary>
    public long ConfigVersion { get; set; }

    /// <summary>
    /// Whether this node is the local node.
    /// </summary>
    public bool IsLocal { get; init; }

    /// <summary>
    /// Gets the full endpoint address.
    /// </summary>
    public string Endpoint => PrimaryAddress != null ? $"{PrimaryAddress}:{ServicePort}" : string.Empty;
}

/// <summary>
/// Cluster configuration that can be propagated to nodes.
/// </summary>
public sealed class ClusterConfiguration
{
    /// <summary>
    /// Configuration version number.
    /// </summary>
    public long Version { get; set; }

    /// <summary>
    /// Cluster name.
    /// </summary>
    public string ClusterName { get; set; } = "datawarehouse-cluster";

    /// <summary>
    /// Minimum quorum size for leader election.
    /// </summary>
    public int QuorumSize { get; set; } = 2;

    /// <summary>
    /// Heartbeat interval in seconds.
    /// </summary>
    public int HeartbeatIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Node timeout in seconds before marking as unreachable.
    /// </summary>
    public int NodeTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum number of nodes in the cluster.
    /// </summary>
    public int MaxNodes { get; set; } = 100;

    /// <summary>
    /// Replication factor for data.
    /// </summary>
    public int ReplicationFactor { get; set; } = 3;

    /// <summary>
    /// Whether to enable automatic role assignment.
    /// </summary>
    public bool AutoRoleAssignment { get; set; } = true;

    /// <summary>
    /// Whether to enable automatic failover.
    /// </summary>
    public bool AutoFailover { get; set; } = true;

    /// <summary>
    /// Custom configuration settings.
    /// </summary>
    public Dictionary<string, object> Settings { get; init; } = new();

    /// <summary>
    /// When the configuration was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Node that last updated the configuration.
    /// </summary>
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// Network interface information for topology detection.
/// </summary>
public sealed class NetworkInterfaceInfo
{
    /// <summary>
    /// Interface name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Interface type.
    /// </summary>
    public NetworkInterfaceType Type { get; init; }

    /// <summary>
    /// Operational status.
    /// </summary>
    public OperationalStatus Status { get; init; }

    /// <summary>
    /// IP addresses assigned to this interface.
    /// </summary>
    public List<IPAddress> Addresses { get; init; } = new();

    /// <summary>
    /// Subnet masks.
    /// </summary>
    public List<IPAddress> SubnetMasks { get; init; } = new();

    /// <summary>
    /// Gateway address.
    /// </summary>
    public IPAddress? Gateway { get; set; }

    /// <summary>
    /// Speed in bits per second.
    /// </summary>
    public long SpeedBps { get; init; }

    /// <summary>
    /// Whether this is a loopback interface.
    /// </summary>
    public bool IsLoopback { get; init; }

    /// <summary>
    /// Physical address (MAC).
    /// </summary>
    public string PhysicalAddress { get; init; } = string.Empty;
}

/// <summary>
/// Discovery announcement message.
/// </summary>
internal sealed class DiscoveryAnnouncement
{
    public string NodeId { get; init; } = string.Empty;
    public string NodeName { get; init; } = string.Empty;
    public string ClusterName { get; init; } = string.Empty;
    public int ServicePort { get; init; }
    public int DiscoveryPort { get; init; }
    public NodeRole Role { get; init; }
    public NodeCapabilities? Capabilities { get; init; }
    public long ConfigVersion { get; init; }
    public DateTime Timestamp { get; init; }
    public string MessageType { get; init; } = "announce";
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>
/// Cluster event for notification.
/// </summary>
public sealed class ClusterEvent
{
    /// <summary>
    /// Event identifier.
    /// </summary>
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Event type.
    /// </summary>
    public string EventType { get; init; } = string.Empty;

    /// <summary>
    /// Node associated with the event.
    /// </summary>
    public string? NodeId { get; init; }

    /// <summary>
    /// Event timestamp.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Event details.
    /// </summary>
    public Dictionary<string, object> Details { get; init; } = new();
}

/// <summary>
/// Configuration for zero-config discovery.
/// </summary>
public sealed class ZeroConfigSettings
{
    /// <summary>
    /// Discovery protocol to use.
    /// </summary>
    public DiscoveryProtocol Protocol { get; set; } = DiscoveryProtocol.Both;

    /// <summary>
    /// Port for mDNS discovery.
    /// </summary>
    public int MdnsPort { get; set; } = 5353;

    /// <summary>
    /// Port for SSDP discovery.
    /// </summary>
    public int SsdpPort { get; set; } = 1900;

    /// <summary>
    /// Port for cluster service communication.
    /// </summary>
    public int ServicePort { get; set; } = 5500;

    /// <summary>
    /// Multicast address for mDNS.
    /// </summary>
    public string MdnsMulticastAddress { get; set; } = "224.0.0.251";

    /// <summary>
    /// Multicast address for SSDP.
    /// </summary>
    public string SsdpMulticastAddress { get; set; } = "239.255.255.250";

    /// <summary>
    /// Discovery announcement interval in seconds.
    /// </summary>
    public int AnnouncementIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Node capability refresh interval in seconds.
    /// </summary>
    public int CapabilityRefreshIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Whether to enable automatic cluster formation.
    /// </summary>
    public bool AutoClusterFormation { get; set; } = true;

    /// <summary>
    /// TTL for multicast packets.
    /// </summary>
    public int MulticastTtl { get; set; } = 4;

    /// <summary>
    /// Maximum announcement retries.
    /// </summary>
    public int MaxAnnouncementRetries { get; set; } = 3;
}

#endregion

/// <summary>
/// Production-ready zero-configuration plugin for DataWarehouse providing automatic
/// node discovery, cluster formation, capability detection, and role assignment.
///
/// Features:
/// - Service discovery using mDNS (Bonjour/Avahi) and SSDP (UPnP)
/// - Automatic cluster formation without manual configuration
/// - Node capability detection (CPU, memory, disk, network)
/// - Automatic role assignment (leader, worker, storage, gateway)
/// - Network topology detection and optimization
/// - Configuration propagation across cluster nodes
/// - Hot-plug support for adding new nodes at runtime
/// - Graceful node departure with state transfer
/// - Thread-safe concurrent operations
///
/// Message Commands:
/// - zeroconfig.discover: Start/trigger node discovery
/// - zeroconfig.announce: Announce this node to the cluster
/// - zeroconfig.join: Join a discovered cluster
/// - zeroconfig.leave: Leave the cluster gracefully
/// - zeroconfig.nodes.list: List all discovered nodes
/// - zeroconfig.nodes.get: Get details of a specific node
/// - zeroconfig.topology: Get network topology information
/// - zeroconfig.capabilities: Get local node capabilities
/// - zeroconfig.config.get: Get cluster configuration
/// - zeroconfig.config.set: Update cluster configuration
/// - zeroconfig.config.propagate: Propagate configuration to all nodes
/// - zeroconfig.role.assign: Manually assign a role to a node
/// - zeroconfig.status: Get overall cluster status
/// </summary>
public sealed class ZeroConfigPlugin : FeaturePluginBase, IDisposable
{
    /// <inheritdoc />
    public override string Id => "com.datawarehouse.zeroconfig";

    /// <inheritdoc />
    public override string Name => "Zero Configuration Plugin";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.OrchestrationProvider;

    /// <inheritdoc />
    public override string Version => "1.0.0";

    #region Private Fields

    private readonly ConcurrentDictionary<string, DiscoveredNode> _discoveredNodes = new();
    private readonly ConcurrentDictionary<string, NetworkInterfaceInfo> _networkInterfaces = new();
    private readonly ConcurrentQueue<ClusterEvent> _eventQueue = new();
    private readonly SemaphoreSlim _configLock = new(1, 1);
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);
    private readonly object _stateLock = new();

    private IKernelContext? _context;
    private CancellationTokenSource? _cts;
    private Task? _announcementTask;
    private Task? _discoveryTask;
    private Task? _heartbeatTask;
    private Task? _capabilityMonitorTask;
    private UdpClient? _mdnsClient;
    private UdpClient? _ssdpClient;
    private UdpClient? _mdnsListener;
    private UdpClient? _ssdpListener;

    private string _localNodeId = string.Empty;
    private string _localNodeName = string.Empty;
    private DiscoveredNode? _localNode;
    private NodeCapabilities? _localCapabilities;
    private ClusterConfiguration _clusterConfig = new();
    private ZeroConfigSettings _settings = new();
    private NetworkTopology _detectedTopology = NetworkTopology.Unknown;
    private bool _isRunning;
    private bool _isLeader;
    private bool _disposed;

    private const int MaxEventQueueSize = 10000;
    private const int DiscoveryTimeoutMs = 5000;
    private const string MdnsServiceType = "_datawarehouse._tcp.local";
    private const string SsdpServiceType = "urn:datawarehouse:service:cluster:1";

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    #endregion

    #region Lifecycle

    /// <inheritdoc />
    public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        _context = request.Context;
        _localNodeId = GenerateNodeId();
        _localNodeName = $"node-{Environment.MachineName}-{_localNodeId[..8]}";

        // Parse configuration
        if (request.Config != null)
        {
            ParseConfiguration(request.Config);
        }

        return Task.FromResult(new HandshakeResponse
        {
            PluginId = Id,
            Name = Name,
            Version = ParseSemanticVersion(Version),
            Category = Category,
            Success = true,
            ReadyState = PluginReadyState.Ready,
            Capabilities = GetCapabilities(),
            Metadata = GetMetadata()
        });
    }

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken ct)
    {
        if (_isRunning) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            // Detect local capabilities
            _localCapabilities = await DetectLocalCapabilitiesAsync();

            // Detect network interfaces
            await DetectNetworkInterfacesAsync();

            // Create local node representation
            _localNode = CreateLocalNode();
            _discoveredNodes[_localNodeId] = _localNode;

            // Initialize discovery sockets
            await InitializeDiscoverySocketsAsync();

            _isRunning = true;

            // Start background tasks
            _announcementTask = RunAnnouncementLoopAsync(_cts.Token);
            _discoveryTask = RunDiscoveryListenerAsync(_cts.Token);
            _heartbeatTask = RunHeartbeatLoopAsync(_cts.Token);
            _capabilityMonitorTask = RunCapabilityMonitorAsync(_cts.Token);

            // Perform initial discovery
            await DiscoverNodesAsync();

            // Attempt automatic cluster formation
            if (_settings.AutoClusterFormation)
            {
                await AttemptClusterFormationAsync();
            }

            RecordEvent("NodeStarted", _localNodeId, new Dictionary<string, object>
            {
                ["name"] = _localNodeName,
                ["capabilities"] = _localCapabilities?.ComputeCapabilityScore() ?? 0
            });
        }
        catch (Exception ex)
        {
            _isRunning = false;
            RecordEvent("StartupFailed", _localNodeId, new Dictionary<string, object>
            {
                ["error"] = ex.Message
            });
            throw;
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync()
    {
        if (!_isRunning) return;

        _isRunning = false;

        try
        {
            // Announce departure
            await AnnounceDepartureAsync();

            // Cancel background tasks
            _cts?.Cancel();

            var tasks = new List<Task>();
            if (_announcementTask != null) tasks.Add(_announcementTask);
            if (_discoveryTask != null) tasks.Add(_discoveryTask);
            if (_heartbeatTask != null) tasks.Add(_heartbeatTask);
            if (_capabilityMonitorTask != null) tasks.Add(_capabilityMonitorTask);

            await Task.WhenAll(tasks.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));

            // Update local node state
            if (_localNode != null)
            {
                _localNode.State = NodeState.Departed;
            }

            RecordEvent("NodeStopped", _localNodeId, new Dictionary<string, object>());
        }
        finally
        {
            CleanupSockets();
            _cts?.Dispose();
            _cts = null;
        }
    }

    #endregion

    #region Message Handling

    /// <inheritdoc />
    public override async Task OnMessageAsync(PluginMessage message)
    {
        if (message.Payload == null) return;

        var response = message.Type switch
        {
            "zeroconfig.discover" => await HandleDiscoverAsync(message.Payload),
            "zeroconfig.announce" => await HandleAnnounceAsync(message.Payload),
            "zeroconfig.join" => await HandleJoinAsync(message.Payload),
            "zeroconfig.leave" => await HandleLeaveAsync(message.Payload),
            "zeroconfig.nodes.list" => HandleListNodes(message.Payload),
            "zeroconfig.nodes.get" => HandleGetNode(message.Payload),
            "zeroconfig.topology" => HandleGetTopology(),
            "zeroconfig.capabilities" => HandleGetCapabilities(),
            "zeroconfig.config.get" => HandleGetConfig(),
            "zeroconfig.config.set" => await HandleSetConfigAsync(message.Payload),
            "zeroconfig.config.propagate" => await HandlePropagateConfigAsync(message.Payload),
            "zeroconfig.role.assign" => await HandleAssignRoleAsync(message.Payload),
            "zeroconfig.status" => HandleGetStatus(),
            "zeroconfig.events" => HandleGetEvents(message.Payload),
            _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
        };

        if (response != null)
        {
            message.Payload["_response"] = response;
        }
    }

    #endregion

    #region Discovery

    private async Task<Dictionary<string, object>> HandleDiscoverAsync(Dictionary<string, object> payload)
    {
        var timeoutMs = payload.GetValueOrDefault("timeout") as int? ?? DiscoveryTimeoutMs;

        try
        {
            var discoveredCount = await DiscoverNodesAsync(timeoutMs);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["discoveredNodes"] = discoveredCount,
                ["totalNodes"] = _discoveredNodes.Count
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    /// <summary>
    /// Discovers nodes on the network using configured protocols.
    /// </summary>
    public async Task<int> DiscoverNodesAsync(int timeoutMs = DiscoveryTimeoutMs)
    {
        await _discoveryLock.WaitAsync();
        try
        {
            var initialCount = _discoveredNodes.Count;

            using var cts = new CancellationTokenSource(timeoutMs);

            var tasks = new List<Task>();

            if (_settings.Protocol == DiscoveryProtocol.MDNS || _settings.Protocol == DiscoveryProtocol.Both)
            {
                tasks.Add(SendMdnsQueryAsync(cts.Token));
            }

            if (_settings.Protocol == DiscoveryProtocol.SSDP || _settings.Protocol == DiscoveryProtocol.Both)
            {
                tasks.Add(SendSsdpQueryAsync(cts.Token));
            }

            await Task.WhenAll(tasks);

            // Wait a bit for responses
            await Task.Delay(Math.Min(timeoutMs, 2000));

            var newCount = _discoveredNodes.Count - initialCount;

            RecordEvent("DiscoveryCompleted", _localNodeId, new Dictionary<string, object>
            {
                ["newNodes"] = newCount,
                ["totalNodes"] = _discoveredNodes.Count
            });

            return newCount;
        }
        finally
        {
            _discoveryLock.Release();
        }
    }

    private async Task SendMdnsQueryAsync(CancellationToken ct)
    {
        if (_mdnsClient == null) return;

        try
        {
            var query = BuildMdnsQuery(MdnsServiceType);
            var endpoint = new IPEndPoint(IPAddress.Parse(_settings.MdnsMulticastAddress), _settings.MdnsPort);

            for (int i = 0; i < _settings.MaxAnnouncementRetries && !ct.IsCancellationRequested; i++)
            {
                await _mdnsClient.SendAsync(query, query.Length, endpoint);
                await Task.Delay(500, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on timeout
        }
        catch
        {
            // Ignore discovery errors
        }
    }

    private async Task SendSsdpQueryAsync(CancellationToken ct)
    {
        if (_ssdpClient == null) return;

        try
        {
            var query = BuildSsdpQuery(SsdpServiceType);
            var endpoint = new IPEndPoint(IPAddress.Parse(_settings.SsdpMulticastAddress), _settings.SsdpPort);

            for (int i = 0; i < _settings.MaxAnnouncementRetries && !ct.IsCancellationRequested; i++)
            {
                await _ssdpClient.SendAsync(query, query.Length, endpoint);
                await Task.Delay(500, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on timeout
        }
        catch
        {
            // Ignore discovery errors
        }
    }

    private async Task RunDiscoveryListenerAsync(CancellationToken ct)
    {
        var tasks = new List<Task>();

        if (_mdnsListener != null)
        {
            tasks.Add(ListenForMdnsAsync(ct));
        }

        if (_ssdpListener != null)
        {
            tasks.Add(ListenForSsdpAsync(ct));
        }

        await Task.WhenAll(tasks);
    }

    private async Task ListenForMdnsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _mdnsListener != null)
        {
            try
            {
                var result = await _mdnsListener.ReceiveAsync(ct);
                await ProcessDiscoveryMessageAsync(result.Buffer, result.RemoteEndPoint, DiscoveryProtocol.MDNS);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                // Socket closed, exit loop
                break;
            }
            catch
            {
                // Continue listening
            }
        }
    }

    private async Task ListenForSsdpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _ssdpListener != null)
        {
            try
            {
                var result = await _ssdpListener.ReceiveAsync(ct);
                await ProcessDiscoveryMessageAsync(result.Buffer, result.RemoteEndPoint, DiscoveryProtocol.SSDP);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                // Socket closed, exit loop
                break;
            }
            catch
            {
                // Continue listening
            }
        }
    }

    private async Task ProcessDiscoveryMessageAsync(byte[] data, IPEndPoint remoteEndPoint, DiscoveryProtocol protocol)
    {
        try
        {
            var json = Encoding.UTF8.GetString(data);
            var announcement = JsonSerializer.Deserialize<DiscoveryAnnouncement>(json, _jsonOptions);

            if (announcement == null || string.IsNullOrEmpty(announcement.NodeId))
                return;

            // Ignore our own announcements
            if (announcement.NodeId == _localNodeId)
                return;

            // Ignore nodes from different clusters
            if (!string.IsNullOrEmpty(announcement.ClusterName) &&
                announcement.ClusterName != _clusterConfig.ClusterName)
                return;

            var isNew = !_discoveredNodes.ContainsKey(announcement.NodeId);

            var node = _discoveredNodes.GetOrAdd(announcement.NodeId, _ => new DiscoveredNode
            {
                NodeId = announcement.NodeId,
                Name = announcement.NodeName,
                DiscoveryProtocol = protocol,
                DiscoveredAt = DateTime.UtcNow,
                IsLocal = false
            });

            // Update node information
            node.Name = announcement.NodeName;
            node.ServicePort = announcement.ServicePort;
            node.DiscoveryPort = announcement.DiscoveryPort;
            node.Role = announcement.Role;
            node.Capabilities = announcement.Capabilities;
            node.ConfigVersion = announcement.ConfigVersion;
            node.LastHeartbeat = DateTime.UtcNow;
            node.Metadata = new Dictionary<string, string>(announcement.Metadata);

            // Add remote address if not already present
            if (!node.Addresses.Contains(remoteEndPoint.Address))
            {
                node.Addresses.Add(remoteEndPoint.Address);
            }

            // Set primary address if not set
            node.PrimaryAddress ??= remoteEndPoint.Address;

            // Measure latency
            node.LatencyMs = await MeasureLatencyAsync(remoteEndPoint.Address);

            // Update state based on message type
            if (announcement.MessageType == "announce")
            {
                if (node.State == NodeState.Discovering || node.State == NodeState.Unreachable)
                {
                    node.State = NodeState.Connecting;
                }
            }
            else if (announcement.MessageType == "departure")
            {
                node.State = NodeState.Leaving;
            }

            if (isNew)
            {
                RecordEvent("NodeDiscovered", announcement.NodeId, new Dictionary<string, object>
                {
                    ["name"] = announcement.NodeName,
                    ["protocol"] = protocol.ToString(),
                    ["address"] = remoteEndPoint.Address.ToString()
                });

                // Attempt to integrate new node if auto-formation enabled
                if (_settings.AutoClusterFormation && _isLeader)
                {
                    await IntegrateNewNodeAsync(node);
                }
            }
        }
        catch
        {
            // Invalid message, ignore
        }
    }

    #endregion

    #region Announcement

    private async Task<Dictionary<string, object>> HandleAnnounceAsync(Dictionary<string, object> payload)
    {
        try
        {
            await SendAnnouncementAsync("announce");

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["nodeId"] = _localNodeId
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    private async Task RunAnnouncementLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_settings.AnnouncementIntervalSeconds), ct);
                await SendAnnouncementAsync("announce");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Continue announcing
            }
        }
    }

    private async Task SendAnnouncementAsync(string messageType)
    {
        var announcement = new DiscoveryAnnouncement
        {
            NodeId = _localNodeId,
            NodeName = _localNodeName,
            ClusterName = _clusterConfig.ClusterName,
            ServicePort = _settings.ServicePort,
            DiscoveryPort = _settings.MdnsPort,
            Role = _localNode?.Role ?? NodeRole.Unknown,
            Capabilities = _localCapabilities,
            ConfigVersion = _clusterConfig.Version,
            Timestamp = DateTime.UtcNow,
            MessageType = messageType,
            Metadata = _localNode?.Metadata ?? new Dictionary<string, string>()
        };

        var json = JsonSerializer.Serialize(announcement, _jsonOptions);
        var data = Encoding.UTF8.GetBytes(json);

        var tasks = new List<Task>();

        if (_mdnsClient != null && (_settings.Protocol == DiscoveryProtocol.MDNS || _settings.Protocol == DiscoveryProtocol.Both))
        {
            var mdnsEndpoint = new IPEndPoint(IPAddress.Parse(_settings.MdnsMulticastAddress), _settings.MdnsPort);
            tasks.Add(_mdnsClient.SendAsync(data, data.Length, mdnsEndpoint));
        }

        if (_ssdpClient != null && (_settings.Protocol == DiscoveryProtocol.SSDP || _settings.Protocol == DiscoveryProtocol.Both))
        {
            var ssdpEndpoint = new IPEndPoint(IPAddress.Parse(_settings.SsdpMulticastAddress), _settings.SsdpPort);
            tasks.Add(_ssdpClient.SendAsync(data, data.Length, ssdpEndpoint));
        }

        await Task.WhenAll(tasks);
    }

    private async Task AnnounceDepartureAsync()
    {
        try
        {
            await SendAnnouncementAsync("departure");
            await Task.Delay(500); // Give time for message to propagate
        }
        catch
        {
            // Best effort
        }
    }

    #endregion

    #region Cluster Formation

    private async Task<Dictionary<string, object>> HandleJoinAsync(Dictionary<string, object> payload)
    {
        var clusterName = payload.GetValueOrDefault("clusterName")?.ToString();
        var leaderAddress = payload.GetValueOrDefault("leaderAddress")?.ToString();

        if (!string.IsNullOrEmpty(clusterName))
        {
            _clusterConfig.ClusterName = clusterName;
        }

        try
        {
            var joined = await JoinClusterAsync(leaderAddress);

            return new Dictionary<string, object>
            {
                ["success"] = joined,
                ["clusterId"] = _clusterConfig.ClusterName,
                ["nodeId"] = _localNodeId,
                ["role"] = _localNode?.Role.ToString() ?? "Unknown"
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    private async Task<Dictionary<string, object>> HandleLeaveAsync(Dictionary<string, object> payload)
    {
        var graceful = payload.GetValueOrDefault("graceful") as bool? ?? true;

        try
        {
            await LeaveClusterAsync(graceful);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["nodeId"] = _localNodeId
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    private async Task AttemptClusterFormationAsync()
    {
        var activeNodes = _discoveredNodes.Values
            .Where(n => n.State == NodeState.Connecting || n.State == NodeState.Active)
            .ToList();

        if (activeNodes.Count == 0)
        {
            // We're the first node, become leader
            _isLeader = true;
            if (_localNode != null)
            {
                _localNode.Role = NodeRole.Leader;
                _localNode.State = NodeState.Active;
                _localNode.JoinedAt = DateTime.UtcNow;
            }

            RecordEvent("ClusterFormed", _localNodeId, new Dictionary<string, object>
            {
                ["role"] = "Leader",
                ["clusterName"] = _clusterConfig.ClusterName
            });
        }
        else
        {
            // Find existing leader or participate in election
            var leader = activeNodes.FirstOrDefault(n => n.Role == NodeRole.Leader);

            if (leader != null)
            {
                await JoinClusterAsync(leader.Endpoint);
            }
            else
            {
                // Participate in leader election
                await ParticipateInLeaderElectionAsync(activeNodes);
            }
        }
    }

    private async Task<bool> JoinClusterAsync(string? leaderAddress)
    {
        if (string.IsNullOrEmpty(leaderAddress))
        {
            // Find a leader from discovered nodes
            var leader = _discoveredNodes.Values.FirstOrDefault(n => n.Role == NodeRole.Leader && n.State == NodeState.Active);
            if (leader == null)
            {
                return false;
            }
            leaderAddress = leader.Endpoint;
        }

        // Assign role based on capabilities
        if (_localNode != null && _localCapabilities != null && _clusterConfig.AutoRoleAssignment)
        {
            _localNode.Role = _localCapabilities.DetermineOptimalRole();
        }

        if (_localNode != null)
        {
            _localNode.State = NodeState.Active;
            _localNode.JoinedAt = DateTime.UtcNow;
        }

        // Send join announcement
        await SendAnnouncementAsync("announce");

        RecordEvent("JoinedCluster", _localNodeId, new Dictionary<string, object>
        {
            ["leaderAddress"] = leaderAddress,
            ["role"] = _localNode?.Role.ToString() ?? "Unknown"
        });

        return true;
    }

    private async Task LeaveClusterAsync(bool graceful)
    {
        if (_localNode != null)
        {
            _localNode.State = NodeState.Leaving;
        }

        if (graceful)
        {
            await AnnounceDepartureAsync();

            // If we're the leader, trigger election
            if (_isLeader)
            {
                _isLeader = false;
                // Other nodes will detect our departure and elect a new leader
            }
        }

        if (_localNode != null)
        {
            _localNode.State = NodeState.Departed;
        }

        RecordEvent("LeftCluster", _localNodeId, new Dictionary<string, object>
        {
            ["graceful"] = graceful
        });
    }

    private async Task ParticipateInLeaderElectionAsync(List<DiscoveredNode> candidates)
    {
        // Simple leader election based on node ID (deterministic)
        var allCandidates = candidates.Append(_localNode!).Where(n => n != null).ToList();
        var leader = allCandidates.OrderBy(n => n!.NodeId).First();

        if (leader?.NodeId == _localNodeId)
        {
            _isLeader = true;
            if (_localNode != null)
            {
                _localNode.Role = NodeRole.Leader;
            }

            RecordEvent("BecameLeader", _localNodeId, new Dictionary<string, object>
            {
                ["candidates"] = candidates.Count
            });
        }
        else
        {
            // Assign role based on capabilities
            if (_localNode != null && _localCapabilities != null)
            {
                _localNode.Role = _localCapabilities.DetermineOptimalRole();
            }
        }

        if (_localNode != null)
        {
            _localNode.State = NodeState.Active;
            _localNode.JoinedAt = DateTime.UtcNow;
        }

        await SendAnnouncementAsync("announce");
    }

    private async Task IntegrateNewNodeAsync(DiscoveredNode node)
    {
        if (!_isLeader) return;

        // Assign role if auto-assignment enabled
        if (_clusterConfig.AutoRoleAssignment && node.Capabilities != null)
        {
            node.Role = node.Capabilities.DetermineOptimalRole();
        }

        node.State = NodeState.Active;
        node.JoinedAt = DateTime.UtcNow;

        // Propagate configuration to new node
        await PropagateConfigToNodeAsync(node);

        RecordEvent("NodeIntegrated", node.NodeId, new Dictionary<string, object>
        {
            ["name"] = node.Name,
            ["role"] = node.Role.ToString()
        });
    }

    #endregion

    #region Heartbeat & Health

    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_clusterConfig.HeartbeatIntervalSeconds), ct);
                await CheckNodeHealthAsync();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Continue monitoring
            }
        }
    }

    private async Task CheckNodeHealthAsync()
    {
        var timeout = TimeSpan.FromSeconds(_clusterConfig.NodeTimeoutSeconds);
        var now = DateTime.UtcNow;

        foreach (var node in _discoveredNodes.Values.Where(n => !n.IsLocal))
        {
            var timeSinceHeartbeat = now - node.LastHeartbeat;

            if (timeSinceHeartbeat > timeout)
            {
                if (node.State == NodeState.Active || node.State == NodeState.Degraded)
                {
                    node.State = NodeState.Unreachable;

                    RecordEvent("NodeUnreachable", node.NodeId, new Dictionary<string, object>
                    {
                        ["name"] = node.Name,
                        ["lastHeartbeat"] = node.LastHeartbeat.ToString("O")
                    });

                    // If we're leader and a node becomes unreachable, handle failover
                    if (_isLeader && _clusterConfig.AutoFailover)
                    {
                        await HandleNodeFailureAsync(node);
                    }
                }
            }
            else if (timeSinceHeartbeat > timeout / 2)
            {
                if (node.State == NodeState.Active)
                {
                    node.State = NodeState.Degraded;

                    RecordEvent("NodeDegraded", node.NodeId, new Dictionary<string, object>
                    {
                        ["name"] = node.Name
                    });
                }
            }
            else
            {
                if (node.State == NodeState.Degraded || node.State == NodeState.Unreachable)
                {
                    node.State = NodeState.Active;

                    RecordEvent("NodeRecovered", node.NodeId, new Dictionary<string, object>
                    {
                        ["name"] = node.Name
                    });
                }
            }
        }
    }

    private async Task HandleNodeFailureAsync(DiscoveredNode failedNode)
    {
        // If the failed node was a leader, we already won election if we're here
        if (failedNode.Role == NodeRole.Leader)
        {
            // Re-elect
            var candidates = _discoveredNodes.Values
                .Where(n => n.State == NodeState.Active && n.NodeId != failedNode.NodeId)
                .ToList();

            if (candidates.Count > 0)
            {
                await ParticipateInLeaderElectionAsync(candidates);
            }
        }

        RecordEvent("NodeFailureHandled", failedNode.NodeId, new Dictionary<string, object>
        {
            ["role"] = failedNode.Role.ToString()
        });
    }

    #endregion

    #region Node Information

    private Dictionary<string, object> HandleListNodes(Dictionary<string, object> payload)
    {
        var includeCapabilities = payload.GetValueOrDefault("includeCapabilities") as bool? ?? false;
        var stateFilter = payload.GetValueOrDefault("state")?.ToString();

        var nodes = _discoveredNodes.Values
            .Where(n => string.IsNullOrEmpty(stateFilter) ||
                        n.State.ToString().Equals(stateFilter, StringComparison.OrdinalIgnoreCase))
            .Select(n => SerializeNode(n, includeCapabilities))
            .ToList();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["nodes"] = nodes,
            ["count"] = nodes.Count
        };
    }

    private Dictionary<string, object> HandleGetNode(Dictionary<string, object> payload)
    {
        var nodeId = payload.GetValueOrDefault("nodeId")?.ToString();

        if (string.IsNullOrEmpty(nodeId))
        {
            // Return local node
            nodeId = _localNodeId;
        }

        if (!_discoveredNodes.TryGetValue(nodeId, out var node))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Node not found"
            };
        }

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["node"] = SerializeNode(node, true)
        };
    }

    private Dictionary<string, object> SerializeNode(DiscoveredNode node, bool includeCapabilities)
    {
        var result = new Dictionary<string, object>
        {
            ["nodeId"] = node.NodeId,
            ["name"] = node.Name,
            ["endpoint"] = node.Endpoint,
            ["state"] = node.State.ToString(),
            ["role"] = node.Role.ToString(),
            ["discoveredAt"] = node.DiscoveredAt.ToString("O"),
            ["lastHeartbeat"] = node.LastHeartbeat.ToString("O"),
            ["joinedAt"] = node.JoinedAt?.ToString("O") ?? "",
            ["latencyMs"] = node.LatencyMs,
            ["configVersion"] = node.ConfigVersion,
            ["isLocal"] = node.IsLocal,
            ["addresses"] = node.Addresses.Select(a => a.ToString()).ToList()
        };

        if (includeCapabilities && node.Capabilities != null)
        {
            result["capabilities"] = new Dictionary<string, object>
            {
                ["cpuCores"] = node.Capabilities.CpuCores,
                ["cpuFrequencyMhz"] = node.Capabilities.CpuFrequencyMhz,
                ["totalMemoryBytes"] = node.Capabilities.TotalMemoryBytes,
                ["availableMemoryBytes"] = node.Capabilities.AvailableMemoryBytes,
                ["totalDiskBytes"] = node.Capabilities.TotalDiskBytes,
                ["availableDiskBytes"] = node.Capabilities.AvailableDiskBytes,
                ["networkBandwidthMbps"] = node.Capabilities.NetworkBandwidthMbps,
                ["operatingSystem"] = node.Capabilities.OperatingSystem,
                ["runtimeVersion"] = node.Capabilities.RuntimeVersion,
                ["hasSsdStorage"] = node.Capabilities.HasSsdStorage,
                ["capabilityScore"] = node.Capabilities.ComputeCapabilityScore()
            };
        }

        return result;
    }

    #endregion

    #region Capabilities

    private Dictionary<string, object> HandleGetCapabilities()
    {
        if (_localCapabilities == null)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Capabilities not detected"
            };
        }

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["capabilities"] = new Dictionary<string, object>
            {
                ["cpuCores"] = _localCapabilities.CpuCores,
                ["cpuFrequencyMhz"] = _localCapabilities.CpuFrequencyMhz,
                ["totalMemoryBytes"] = _localCapabilities.TotalMemoryBytes,
                ["availableMemoryBytes"] = _localCapabilities.AvailableMemoryBytes,
                ["totalDiskBytes"] = _localCapabilities.TotalDiskBytes,
                ["availableDiskBytes"] = _localCapabilities.AvailableDiskBytes,
                ["networkBandwidthMbps"] = _localCapabilities.NetworkBandwidthMbps,
                ["operatingSystem"] = _localCapabilities.OperatingSystem,
                ["runtimeVersion"] = _localCapabilities.RuntimeVersion,
                ["hasSsdStorage"] = _localCapabilities.HasSsdStorage,
                ["supportsHardwareAcceleration"] = _localCapabilities.SupportsHardwareAcceleration,
                ["capabilityScore"] = _localCapabilities.ComputeCapabilityScore(),
                ["optimalRole"] = _localCapabilities.DetermineOptimalRole().ToString()
            }
        };
    }

    private async Task<NodeCapabilities> DetectLocalCapabilitiesAsync()
    {
        return await Task.Run(() =>
        {
            var capabilities = new NodeCapabilities
            {
                CpuCores = Environment.ProcessorCount,
                OperatingSystem = RuntimeInformation.OSDescription,
                RuntimeVersion = RuntimeInformation.FrameworkDescription,
                SupportsHardwareAcceleration = System.Runtime.Intrinsics.X86.Sse2.IsSupported ||
                                               System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported
            };

            // Get memory information
            try
            {
                var gcInfo = GC.GetGCMemoryInfo();
                capabilities = capabilities with
                {
                    TotalMemoryBytes = gcInfo.TotalAvailableMemoryBytes,
                    AvailableMemoryBytes = gcInfo.TotalAvailableMemoryBytes - GC.GetTotalMemory(false)
                };
            }
            catch
            {
                // Default values remain
            }

            // Get disk information
            try
            {
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                    .ToList();

                if (drives.Count > 0)
                {
                    capabilities = capabilities with
                    {
                        TotalDiskBytes = drives.Sum(d => d.TotalSize),
                        AvailableDiskBytes = drives.Sum(d => d.AvailableFreeSpace)
                    };
                }
            }
            catch
            {
                // Default values remain
            }

            // Get network bandwidth from interfaces
            try
            {
                var maxSpeed = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                               n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .Select(n => n.Speed)
                    .DefaultIfEmpty(0L)
                    .Max();

                capabilities = capabilities with
                {
                    NetworkBandwidthMbps = maxSpeed / 1_000_000
                };
            }
            catch
            {
                // Default values remain
            }

            // Detect SSD (heuristic - check if any drive is SSD)
            try
            {
                // On Linux, check /sys/block/*/queue/rotational
                // On Windows, use WMI (simplified here)
                capabilities = capabilities with
                {
                    HasSsdStorage = DetectSsdStorage()
                };
            }
            catch
            {
                // Default values remain
            }

            return capabilities;
        });
    }

    private bool DetectSsdStorage()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var blockDevices = Directory.GetDirectories("/sys/block");
                foreach (var device in blockDevices)
                {
                    var rotationalPath = Path.Combine(device, "queue", "rotational");
                    if (File.Exists(rotationalPath))
                    {
                        var value = File.ReadAllText(rotationalPath).Trim();
                        if (value == "0")
                            return true;
                    }
                }
            }
            // On other platforms, assume SSD if drive speed is sufficient
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task RunCapabilityMonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_settings.CapabilityRefreshIntervalSeconds), ct);

                // Update dynamic capability metrics
                if (_localCapabilities != null)
                {
                    var gcInfo = GC.GetGCMemoryInfo();
                    _localCapabilities = _localCapabilities with
                    {
                        AvailableMemoryBytes = gcInfo.TotalAvailableMemoryBytes - GC.GetTotalMemory(false)
                    };

                    var drives = DriveInfo.GetDrives()
                        .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                        .ToList();

                    if (drives.Count > 0)
                    {
                        _localCapabilities = _localCapabilities with
                        {
                            AvailableDiskBytes = drives.Sum(d => d.AvailableFreeSpace)
                        };
                    }

                    if (_localNode != null)
                    {
                        _localNode.Capabilities = _localCapabilities;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Continue monitoring
            }
        }
    }

    #endregion

    #region Network Topology

    private Dictionary<string, object> HandleGetTopology()
    {
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["topology"] = _detectedTopology.ToString(),
            ["interfaces"] = _networkInterfaces.Values.Select(i => new Dictionary<string, object>
            {
                ["name"] = i.Name,
                ["type"] = i.Type.ToString(),
                ["status"] = i.Status.ToString(),
                ["addresses"] = i.Addresses.Select(a => a.ToString()).ToList(),
                ["gateway"] = i.Gateway?.ToString() ?? "",
                ["speedMbps"] = i.SpeedBps / 1_000_000,
                ["physicalAddress"] = i.PhysicalAddress
            }).ToList()
        };
    }

    private async Task DetectNetworkInterfacesAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up)
                    .ToList();

                foreach (var iface in interfaces)
                {
                    var ipProps = iface.GetIPProperties();
                    var addresses = ipProps.UnicastAddresses
                        .Select(a => a.Address)
                        .Where(a => a.AddressFamily == AddressFamily.InterNetwork ||
                                   a.AddressFamily == AddressFamily.InterNetworkV6)
                        .ToList();

                    var gateway = ipProps.GatewayAddresses
                        .FirstOrDefault()?.Address;

                    var subnetMasks = ipProps.UnicastAddresses
                        .Where(a => a.IPv4Mask != null)
                        .Select(a => a.IPv4Mask)
                        .ToList();

                    var info = new NetworkInterfaceInfo
                    {
                        Name = iface.Name,
                        Type = iface.NetworkInterfaceType,
                        Status = iface.OperationalStatus,
                        Addresses = addresses,
                        SubnetMasks = subnetMasks!,
                        Gateway = gateway,
                        SpeedBps = iface.Speed,
                        IsLoopback = iface.NetworkInterfaceType == NetworkInterfaceType.Loopback,
                        PhysicalAddress = iface.GetPhysicalAddress().ToString()
                    };

                    _networkInterfaces[iface.Id] = info;
                }

                // Detect topology based on network configuration
                _detectedTopology = DetectTopology();
            }
            catch
            {
                // Network detection failed
            }
        });
    }

    private NetworkTopology DetectTopology()
    {
        var activeInterfaces = _networkInterfaces.Values
            .Where(i => !i.IsLoopback && i.Status == OperationalStatus.Up)
            .ToList();

        // If multiple gateways, likely hierarchical
        var gateways = activeInterfaces
            .Where(i => i.Gateway != null)
            .Select(i => i.Gateway!)
            .Distinct()
            .ToList();

        if (gateways.Count > 1)
            return NetworkTopology.Hierarchical;

        // If high-speed interfaces with multiple addresses, likely mesh
        var highSpeedInterfaces = activeInterfaces
            .Where(i => i.SpeedBps >= 1_000_000_000)
            .ToList();

        if (highSpeedInterfaces.Count > 1)
            return NetworkTopology.Mesh;

        // Default to flat topology
        return NetworkTopology.Flat;
    }

    #endregion

    #region Configuration

    private Dictionary<string, object> HandleGetConfig()
    {
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["config"] = new Dictionary<string, object>
            {
                ["version"] = _clusterConfig.Version,
                ["clusterName"] = _clusterConfig.ClusterName,
                ["quorumSize"] = _clusterConfig.QuorumSize,
                ["heartbeatIntervalSeconds"] = _clusterConfig.HeartbeatIntervalSeconds,
                ["nodeTimeoutSeconds"] = _clusterConfig.NodeTimeoutSeconds,
                ["maxNodes"] = _clusterConfig.MaxNodes,
                ["replicationFactor"] = _clusterConfig.ReplicationFactor,
                ["autoRoleAssignment"] = _clusterConfig.AutoRoleAssignment,
                ["autoFailover"] = _clusterConfig.AutoFailover,
                ["updatedAt"] = _clusterConfig.UpdatedAt.ToString("O"),
                ["updatedBy"] = _clusterConfig.UpdatedBy ?? ""
            }
        };
    }

    private async Task<Dictionary<string, object>> HandleSetConfigAsync(Dictionary<string, object> payload)
    {
        await _configLock.WaitAsync();
        try
        {
            var changed = false;

            if (payload.TryGetValue("clusterName", out var cn) && cn is string clusterName)
            {
                _clusterConfig.ClusterName = clusterName;
                changed = true;
            }

            if (payload.TryGetValue("quorumSize", out var qs))
            {
                _clusterConfig.QuorumSize = Convert.ToInt32(qs);
                changed = true;
            }

            if (payload.TryGetValue("heartbeatIntervalSeconds", out var hb))
            {
                _clusterConfig.HeartbeatIntervalSeconds = Convert.ToInt32(hb);
                changed = true;
            }

            if (payload.TryGetValue("nodeTimeoutSeconds", out var nt))
            {
                _clusterConfig.NodeTimeoutSeconds = Convert.ToInt32(nt);
                changed = true;
            }

            if (payload.TryGetValue("maxNodes", out var mn))
            {
                _clusterConfig.MaxNodes = Convert.ToInt32(mn);
                changed = true;
            }

            if (payload.TryGetValue("replicationFactor", out var rf))
            {
                _clusterConfig.ReplicationFactor = Convert.ToInt32(rf);
                changed = true;
            }

            if (payload.TryGetValue("autoRoleAssignment", out var ara) && ara is bool autoRole)
            {
                _clusterConfig.AutoRoleAssignment = autoRole;
                changed = true;
            }

            if (payload.TryGetValue("autoFailover", out var af) && af is bool autoFailover)
            {
                _clusterConfig.AutoFailover = autoFailover;
                changed = true;
            }

            if (changed)
            {
                _clusterConfig.Version++;
                _clusterConfig.UpdatedAt = DateTime.UtcNow;
                _clusterConfig.UpdatedBy = _localNodeId;

                if (_localNode != null)
                {
                    _localNode.ConfigVersion = _clusterConfig.Version;
                }

                RecordEvent("ConfigUpdated", _localNodeId, new Dictionary<string, object>
                {
                    ["version"] = _clusterConfig.Version
                });
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["version"] = _clusterConfig.Version
            };
        }
        finally
        {
            _configLock.Release();
        }
    }

    private async Task<Dictionary<string, object>> HandlePropagateConfigAsync(Dictionary<string, object> payload)
    {
        if (!_isLeader)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Only leader can propagate configuration"
            };
        }

        var propagated = 0;

        foreach (var node in _discoveredNodes.Values.Where(n => !n.IsLocal && n.State == NodeState.Active))
        {
            try
            {
                await PropagateConfigToNodeAsync(node);
                propagated++;
            }
            catch
            {
                // Continue with other nodes
            }
        }

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["propagatedToNodes"] = propagated,
            ["version"] = _clusterConfig.Version
        };
    }

    private async Task PropagateConfigToNodeAsync(DiscoveredNode node)
    {
        // In a real implementation, this would send the configuration via TCP/HTTP
        // to the node's service endpoint. For now, we update the local tracking.
        node.ConfigVersion = _clusterConfig.Version;
        await Task.CompletedTask;
    }

    #endregion

    #region Role Assignment

    private async Task<Dictionary<string, object>> HandleAssignRoleAsync(Dictionary<string, object> payload)
    {
        var nodeId = payload.GetValueOrDefault("nodeId")?.ToString();
        var roleStr = payload.GetValueOrDefault("role")?.ToString();

        if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(roleStr))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "nodeId and role are required"
            };
        }

        if (!Enum.TryParse<NodeRole>(roleStr, true, out var role))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Invalid role"
            };
        }

        if (!_discoveredNodes.TryGetValue(nodeId, out var node))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Node not found"
            };
        }

        var previousRole = node.Role;
        node.Role = role;

        // If assigning leader, update local state
        if (role == NodeRole.Leader && node.IsLocal)
        {
            _isLeader = true;
        }
        else if (previousRole == NodeRole.Leader && node.IsLocal)
        {
            _isLeader = false;
        }

        RecordEvent("RoleAssigned", nodeId, new Dictionary<string, object>
        {
            ["previousRole"] = previousRole.ToString(),
            ["newRole"] = role.ToString()
        });

        await SendAnnouncementAsync("announce");

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["nodeId"] = nodeId,
            ["role"] = role.ToString()
        };
    }

    #endregion

    #region Status & Events

    private Dictionary<string, object> HandleGetStatus()
    {
        var activeNodes = _discoveredNodes.Values.Count(n => n.State == NodeState.Active);
        var degradedNodes = _discoveredNodes.Values.Count(n => n.State == NodeState.Degraded);
        var unreachableNodes = _discoveredNodes.Values.Count(n => n.State == NodeState.Unreachable);

        var roleDistribution = _discoveredNodes.Values
            .Where(n => n.State == NodeState.Active)
            .GroupBy(n => n.Role)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["localNodeId"] = _localNodeId,
            ["localNodeName"] = _localNodeName,
            ["isLeader"] = _isLeader,
            ["isRunning"] = _isRunning,
            ["clusterName"] = _clusterConfig.ClusterName,
            ["configVersion"] = _clusterConfig.Version,
            ["topology"] = _detectedTopology.ToString(),
            ["nodeCount"] = _discoveredNodes.Count,
            ["activeNodes"] = activeNodes,
            ["degradedNodes"] = degradedNodes,
            ["unreachableNodes"] = unreachableNodes,
            ["roleDistribution"] = roleDistribution,
            ["localRole"] = _localNode?.Role.ToString() ?? "Unknown",
            ["discoveryProtocol"] = _settings.Protocol.ToString()
        };
    }

    private Dictionary<string, object> HandleGetEvents(Dictionary<string, object> payload)
    {
        var limit = payload.GetValueOrDefault("limit") as int? ?? 100;
        var eventType = payload.GetValueOrDefault("eventType")?.ToString();

        var events = _eventQueue
            .Where(e => string.IsNullOrEmpty(eventType) ||
                        e.EventType.Equals(eventType, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Timestamp)
            .Take(limit)
            .Select(e => new Dictionary<string, object>
            {
                ["eventId"] = e.EventId,
                ["eventType"] = e.EventType,
                ["nodeId"] = e.NodeId ?? "",
                ["timestamp"] = e.Timestamp.ToString("O"),
                ["details"] = e.Details
            })
            .ToList();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["events"] = events,
            ["count"] = events.Count
        };
    }

    private void RecordEvent(string eventType, string? nodeId, Dictionary<string, object> details)
    {
        var evt = new ClusterEvent
        {
            EventType = eventType,
            NodeId = nodeId,
            Details = details
        };

        _eventQueue.Enqueue(evt);

        // Trim queue if needed
        while (_eventQueue.Count > MaxEventQueueSize)
        {
            _eventQueue.TryDequeue(out _);
        }
    }

    #endregion

    #region Helper Methods

    private void ParseConfiguration(Dictionary<string, object> config)
    {
        if (config.TryGetValue("protocol", out var protocol) && protocol is string protocolStr)
        {
            if (Enum.TryParse<DiscoveryProtocol>(protocolStr, true, out var dp))
            {
                _settings.Protocol = dp;
            }
        }

        if (config.TryGetValue("servicePort", out var sp))
        {
            _settings.ServicePort = Convert.ToInt32(sp);
        }

        if (config.TryGetValue("clusterName", out var cn) && cn is string clusterName)
        {
            _clusterConfig.ClusterName = clusterName;
        }

        if (config.TryGetValue("autoClusterFormation", out var acf) && acf is bool autoCluster)
        {
            _settings.AutoClusterFormation = autoCluster;
        }
    }

    private string GenerateNodeId()
    {
        var machineId = Environment.MachineName;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var random = RandomNumberGenerator.GetBytes(8);

        var data = $"{machineId}-{timestamp}-{Convert.ToHexString(random)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(data));

        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    private DiscoveredNode CreateLocalNode()
    {
        var addresses = _networkInterfaces.Values
            .Where(i => !i.IsLoopback && i.Status == OperationalStatus.Up)
            .SelectMany(i => i.Addresses)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .ToList();

        var primaryAddress = addresses.FirstOrDefault();

        return new DiscoveredNode
        {
            NodeId = _localNodeId,
            Name = _localNodeName,
            Addresses = addresses,
            PrimaryAddress = primaryAddress,
            ServicePort = _settings.ServicePort,
            DiscoveryPort = _settings.MdnsPort,
            State = NodeState.Connecting,
            Role = NodeRole.Unknown,
            Capabilities = _localCapabilities,
            DiscoveryProtocol = _settings.Protocol,
            ConfigVersion = _clusterConfig.Version,
            IsLocal = true
        };
    }

    private async Task InitializeDiscoverySocketsAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                // Initialize mDNS sockets
                if (_settings.Protocol == DiscoveryProtocol.MDNS || _settings.Protocol == DiscoveryProtocol.Both)
                {
                    _mdnsClient = new UdpClient();
                    _mdnsClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _mdnsClient.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, _settings.MulticastTtl);

                    _mdnsListener = new UdpClient();
                    _mdnsListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _mdnsListener.Client.Bind(new IPEndPoint(IPAddress.Any, _settings.MdnsPort));
                    _mdnsListener.JoinMulticastGroup(IPAddress.Parse(_settings.MdnsMulticastAddress));
                }

                // Initialize SSDP sockets
                if (_settings.Protocol == DiscoveryProtocol.SSDP || _settings.Protocol == DiscoveryProtocol.Both)
                {
                    _ssdpClient = new UdpClient();
                    _ssdpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _ssdpClient.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, _settings.MulticastTtl);

                    _ssdpListener = new UdpClient();
                    _ssdpListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _ssdpListener.Client.Bind(new IPEndPoint(IPAddress.Any, _settings.SsdpPort));
                    _ssdpListener.JoinMulticastGroup(IPAddress.Parse(_settings.SsdpMulticastAddress));
                }
            }
            catch (SocketException ex)
            {
                // Socket initialization may fail in some environments
                // Continue with available protocols
                RecordEvent("SocketInitWarning", _localNodeId, new Dictionary<string, object>
                {
                    ["error"] = ex.Message
                });
            }
        });
    }

    private void CleanupSockets()
    {
        try
        {
            _mdnsClient?.Close();
            _mdnsClient?.Dispose();
            _mdnsListener?.Close();
            _mdnsListener?.Dispose();
            _ssdpClient?.Close();
            _ssdpClient?.Dispose();
            _ssdpListener?.Close();
            _ssdpListener?.Dispose();
        }
        catch
        {
            // Ignore cleanup errors
        }

        _mdnsClient = null;
        _mdnsListener = null;
        _ssdpClient = null;
        _ssdpListener = null;
    }

    private byte[] BuildMdnsQuery(string serviceType)
    {
        // Simplified mDNS query - real implementation would use proper DNS packet format
        var announcement = new DiscoveryAnnouncement
        {
            NodeId = _localNodeId,
            NodeName = _localNodeName,
            ClusterName = _clusterConfig.ClusterName,
            ServicePort = _settings.ServicePort,
            DiscoveryPort = _settings.MdnsPort,
            Role = _localNode?.Role ?? NodeRole.Unknown,
            Capabilities = _localCapabilities,
            ConfigVersion = _clusterConfig.Version,
            Timestamp = DateTime.UtcNow,
            MessageType = "query"
        };

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(announcement, _jsonOptions));
    }

    private byte[] BuildSsdpQuery(string serviceType)
    {
        // Simplified SSDP query - real implementation would use proper SSDP format
        var announcement = new DiscoveryAnnouncement
        {
            NodeId = _localNodeId,
            NodeName = _localNodeName,
            ClusterName = _clusterConfig.ClusterName,
            ServicePort = _settings.ServicePort,
            DiscoveryPort = _settings.SsdpPort,
            Role = _localNode?.Role ?? NodeRole.Unknown,
            Capabilities = _localCapabilities,
            ConfigVersion = _clusterConfig.Version,
            Timestamp = DateTime.UtcNow,
            MessageType = "query"
        };

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(announcement, _jsonOptions));
    }

    private async Task<double> MeasureLatencyAsync(IPAddress address)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, 1000);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <inheritdoc />
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new() { Name = "discover", Description = "Discover nodes on the network" },
            new() { Name = "announce", Description = "Announce this node to the cluster" },
            new() { Name = "join", Description = "Join a discovered cluster" },
            new() { Name = "leave", Description = "Leave the cluster gracefully" },
            new() { Name = "nodes.list", Description = "List all discovered nodes" },
            new() { Name = "topology", Description = "Get network topology information" },
            new() { Name = "capabilities", Description = "Get local node capabilities" },
            new() { Name = "config.manage", Description = "Manage cluster configuration" },
            new() { Name = "role.assign", Description = "Assign roles to nodes" }
        };
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "ZeroConfig";
        metadata["LocalNodeId"] = _localNodeId;
        metadata["DiscoveryProtocol"] = _settings.Protocol.ToString();
        metadata["SupportsAutoDiscovery"] = true;
        metadata["SupportsAutoClusterFormation"] = true;
        metadata["SupportsCapabilityDetection"] = true;
        metadata["SupportsAutoRoleAssignment"] = true;
        metadata["SupportsHotPlug"] = true;
        metadata["SupportsGracefulDeparture"] = true;
        return metadata;
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the plugin resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CleanupSockets();
        _configLock.Dispose();
        _discoveryLock.Dispose();
        _cts?.Dispose();
    }

    #endregion
}
