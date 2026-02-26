using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Edge.Mesh;

/// <summary>
/// ZigBee mesh network implementation.
/// </summary>
/// <remarks>
/// Requires a ZigBee hardware adapter and platform-specific SDK
/// (e.g., Texas Instruments Z-Stack, Silicon Labs Ember ZNet, Digi XBee API).
/// Configure the adapter via MeshAdapterOptions and register a platform-specific IMeshNetwork implementation.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Zigbee mesh (EDGE-08)")]
public sealed class ZigbeeMesh : IMeshNetwork
{
    public bool IsInitialized => false;
    // Events are interface requirements; they are never raised because all operations throw PlatformNotSupportedException.
#pragma warning disable CS0067
    public event EventHandler<MeshMessageReceivedEventArgs>? OnMessageReceived;
    public event EventHandler<MeshTopologyChangedEventArgs>? OnTopologyChanged;
#pragma warning restore CS0067

    public Task InitializeAsync(MeshSettings settings, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException(
            "ZigBee mesh requires a ZigBee hardware adapter (e.g., TI Z-Stack, Silicon Labs Ember ZNet, Digi XBee). " +
            "Configure via MeshAdapterOptions and register a platform-specific IMeshNetwork implementation.");

    public Task SendMessageAsync(int destinationNodeId, byte[] payload, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException(
            "ZigBee mesh requires a ZigBee hardware adapter. " +
            "Configure via MeshAdapterOptions and register a platform-specific IMeshNetwork implementation.");

    public Task<MeshTopology> DiscoverTopologyAsync(CancellationToken ct = default) =>
        throw new PlatformNotSupportedException(
            "ZigBee mesh requires a ZigBee hardware adapter. " +
            "Configure via MeshAdapterOptions and register a platform-specific IMeshNetwork implementation.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
