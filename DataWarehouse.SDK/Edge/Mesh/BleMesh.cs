using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Edge.Mesh;

/// <summary>
/// BLE mesh network implementation.
/// </summary>
/// <remarks>
/// Requires a hardware BLE adapter and platform-specific BLE Mesh SDK
/// (e.g., Nordic nRF5 SDK for Mesh, Zephyr RTOS BLE Mesh, Bluetooth SIG Mesh Profile).
/// Configure the adapter via MeshAdapterOptions and register a platform-specific IMeshNetwork implementation.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: BLE mesh (EDGE-08)")]
public sealed class BleMesh : IMeshNetwork
{
    public bool IsInitialized => false;
    // Events are interface requirements; they are never raised because all operations throw PlatformNotSupportedException.
#pragma warning disable CS0067
    public event EventHandler<MeshMessageReceivedEventArgs>? OnMessageReceived;
    public event EventHandler<MeshTopologyChangedEventArgs>? OnTopologyChanged;
#pragma warning restore CS0067

    public Task InitializeAsync(MeshSettings settings, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException(
            "BLE mesh requires a BLE hardware adapter (e.g., Nordic nRF5 SDK for Mesh, Zephyr RTOS BLE Mesh). " +
            "Configure via MeshAdapterOptions and register a platform-specific IMeshNetwork implementation.");

    public Task SendMessageAsync(int destinationNodeId, byte[] payload, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException(
            "BLE mesh requires a BLE hardware adapter. " +
            "Configure via MeshAdapterOptions and register a platform-specific IMeshNetwork implementation.");

    public Task<MeshTopology> DiscoverTopologyAsync(CancellationToken ct = default) =>
        throw new PlatformNotSupportedException(
            "BLE mesh requires a BLE hardware adapter. " +
            "Configure via MeshAdapterOptions and register a platform-specific IMeshNetwork implementation.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
