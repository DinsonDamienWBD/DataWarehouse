using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Edge.Mesh;

namespace DataWarehouse.Plugins.AedsCore.Adapters;

/// <summary>
/// AEDS adapter for mesh network communication (Phase 36, EDGE-08).
/// </summary>
/// <remarks>
/// <para>
/// Integrates Zigbee, LoRa, and BLE mesh networks into the AEDS (Adaptive Edge Distribution System)
/// plugin, enabling low-power sensor networks to communicate with central DataWarehouse clusters.
/// </para>
/// <para>
/// <strong>Data Flow:</strong>
/// <list type="bullet">
///   <item><description>Inbound: Mesh messages received via OnMessageReceived → AEDS message bus</description></item>
///   <item><description>Outbound: AEDS messages → SendMessageAsync() → mesh network</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Topology Integration:</strong> Periodically discovers mesh topology and publishes to AEDS
/// for network monitoring and routing decisions. Gateway nodes aggregate data from end devices and
/// forward to central cluster.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Mesh network adapter for AEDS (EDGE-08)")]
public sealed class MeshNetworkAdapter : IDisposable, IAsyncDisposable
{
    private readonly IMeshNetwork _meshNetwork;
    private readonly MeshSettings _settings;
    private readonly Timer? _topologyTimer;

    /// <summary>
    /// Event raised when data is received from the mesh network.
    /// </summary>
    public event EventHandler<DataReceivedEventArgs>? OnDataReceived;

    /// <summary>
    /// Event raised when mesh topology changes.
    /// </summary>
    public event EventHandler<MeshTopology>? OnTopologyChanged;

    /// <summary>
    /// Initializes a new mesh network adapter.
    /// </summary>
    /// <param name="settings">Mesh network settings.</param>
    public MeshNetworkAdapter(MeshSettings settings)
    {
        _settings = settings;

        // Select mesh implementation based on protocol
        _meshNetwork = settings.Protocol switch
        {
            MeshProtocol.Zigbee => new ZigbeeMesh(),
            MeshProtocol.LoRa => new LoRaMesh(),
            MeshProtocol.BLE => new BleMesh(),
            _ => throw new ArgumentException($"Unsupported mesh protocol: {settings.Protocol}")
        };

        _meshNetwork.OnMessageReceived += OnMeshMessageReceived;
        _meshNetwork.OnTopologyChanged += (_, args) => OnTopologyChanged?.Invoke(this, args.NewTopology);

        // Start periodic topology discovery (every 5 minutes)
        _topologyTimer = new Timer(
            callback: async _ => { try { await DiscoverTopologyAsync(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Timer callback failed: {ex.Message}"); } },
            state: null,
            dueTime: TimeSpan.FromMinutes(1),
            period: TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Starts the mesh network adapter.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _meshNetwork.InitializeAsync(_settings, ct);
    }

    /// <summary>
    /// Stops the mesh network adapter.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task StopAsync(CancellationToken ct = default)
    {
        await _meshNetwork.DisposeAsync();
    }

    /// <summary>
    /// Sends data to a mesh network node.
    /// </summary>
    /// <param name="data">Data to send.</param>
    /// <param name="destination">Destination node ID as string.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SendDataAsync(byte[] data, string destination, CancellationToken ct = default)
    {
        // Parse destination as node ID
        if (int.TryParse(destination, out var nodeId))
        {
            await _meshNetwork.SendMessageAsync(nodeId, data, ct);
        }
        else
        {
            throw new ArgumentException($"Invalid destination node ID: {destination}");
        }
    }

    /// <summary>
    /// Discovers mesh network topology and publishes topology changed event.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<MeshTopology> DiscoverTopologyAsync(CancellationToken ct = default)
    {
        var topology = await _meshNetwork.DiscoverTopologyAsync(ct);
        OnTopologyChanged?.Invoke(this, topology);
        return topology;
    }

    private void OnMeshMessageReceived(object? sender, MeshMessageReceivedEventArgs e)
    {
        // Forward to AEDS message bus via event
        OnDataReceived?.Invoke(this, new DataReceivedEventArgs(e.Payload, e.SourceNodeId.ToString()));
    }

    public void Dispose()
    {
        _topologyTimer?.Dispose();
        // Don't call async dispose from sync Dispose
        // User must call DisposeAsync() for proper cleanup
    }

    /// <summary>
    /// Asynchronously disposes the mesh network adapter.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _topologyTimer?.Dispose();
        if (_meshNetwork != null)
        {
            await _meshNetwork.DisposeAsync();
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Event arguments for data received events.
/// </summary>
/// <param name="Data">Received data payload.</param>
/// <param name="Source">Source identifier (node ID as string).</param>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Data received event args (EDGE-08)")]
public sealed record DataReceivedEventArgs(byte[] Data, string Source);
