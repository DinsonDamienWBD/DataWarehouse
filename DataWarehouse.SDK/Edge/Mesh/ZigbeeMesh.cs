using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Edge.Mesh;

/// <summary>
/// Zigbee mesh network implementation (stub).
/// </summary>
/// <remarks>
/// Stub implementation demonstrating API contract. Production use requires hardware-specific SDK
/// (e.g., Texas Instruments Z-Stack, Silicon Labs Ember ZNet, Digi XBee API).
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Zigbee mesh stub (EDGE-08)")]
public sealed class ZigbeeMesh : IMeshNetwork
{
    private bool _initialized;

    public bool IsInitialized => _initialized;
    public event EventHandler<MeshMessageReceivedEventArgs>? OnMessageReceived;
    public event EventHandler<MeshTopologyChangedEventArgs>? OnTopologyChanged;

    public Task InitializeAsync(MeshSettings settings, CancellationToken ct = default)
    {
        _initialized = true;
        OnTopologyChanged?.Invoke(this, new MeshTopologyChangedEventArgs(new MeshTopology
        {
            Nodes = Array.Empty<MeshNode>(),
            Links = Array.Empty<MeshLink>(),
            Routes = new Dictionary<int, int[]>()
        }));
        return Task.CompletedTask;
    }

    public Task SendMessageAsync(int destinationNodeId, byte[] payload, CancellationToken ct = default)
    {
        // Stub: in single-node mode, loopback the message to simulate receipt
        OnMessageReceived?.Invoke(this, new MeshMessageReceivedEventArgs(
            SourceNodeId: destinationNodeId,
            Payload: payload));
        return Task.CompletedTask;
    }

    public Task<MeshTopology> DiscoverTopologyAsync(CancellationToken ct = default)
    {
        var topology = new MeshTopology
        {
            Nodes = Array.Empty<MeshNode>(),
            Links = Array.Empty<MeshLink>(),
            Routes = new Dictionary<int, int[]>()
        };
        OnTopologyChanged?.Invoke(this, new MeshTopologyChangedEventArgs(topology));
        return Task.FromResult(topology);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
