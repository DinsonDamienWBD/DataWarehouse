using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Edge.Mesh;

/// <summary>
/// BLE mesh network implementation (stub).
/// </summary>
/// <remarks>
/// Stub implementation demonstrating API contract. Production use requires hardware-specific SDK
/// (e.g., Nordic nRF5 SDK for Mesh, Zephyr RTOS BLE Mesh, Bluetooth SIG Mesh Profile).
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: BLE mesh stub (EDGE-08)")]
public sealed class BleMesh : IMeshNetwork
{
    private bool _initialized;

    public bool IsInitialized => _initialized;
    public event EventHandler<MeshMessageReceivedEventArgs>? OnMessageReceived;
    public event EventHandler<MeshTopologyChangedEventArgs>? OnTopologyChanged;

    public Task InitializeAsync(MeshSettings settings, CancellationToken ct = default)
    {
        _initialized = true;
        return Task.CompletedTask;
    }

    public Task SendMessageAsync(int destinationNodeId, byte[] payload, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<MeshTopology> DiscoverTopologyAsync(CancellationToken ct = default) =>
        Task.FromResult(new MeshTopology
        {
            Nodes = Array.Empty<MeshNode>(),
            Links = Array.Empty<MeshLink>(),
            Routes = new Dictionary<int, int[]>()
        });

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
