using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Edge.Mesh;

/// <summary>
/// LoRa mesh network implementation.
/// </summary>
/// <remarks>
/// <para>
/// Requires hardware-specific SDK integration (e.g., Semtech LoRa SDK, RadioHead library,
/// or direct SPI access to LoRa radios such as SX1276, SX1278, SX1262).
/// Configure the adapter via MeshAdapterOptions and register a platform-specific IMeshNetwork implementation.
/// </para>
/// <para>
/// <strong>LoRa Characteristics:</strong>
/// <list type="bullet">
///   <item><description>Frequency: Sub-GHz (433MHz, 868MHz, 915MHz depending on region)</description></item>
///   <item><description>Range: 2-15km (line-of-sight), 500m-2km (urban)</description></item>
///   <item><description>Data rate: 0.3-50 kbps (spreading factor dependent)</description></item>
///   <item><description>Max payload: ~250 bytes per packet</description></item>
/// </list>
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: LoRa mesh (EDGE-08)")]
public sealed class LoRaMesh : IMeshNetwork
{
    public bool IsInitialized => false;
    // Events are interface requirements; they are never raised because all operations throw PlatformNotSupportedException.
#pragma warning disable CS0067
    public event EventHandler<MeshMessageReceivedEventArgs>? OnMessageReceived;
    public event EventHandler<MeshTopologyChangedEventArgs>? OnTopologyChanged;
#pragma warning restore CS0067

    public Task InitializeAsync(MeshSettings settings, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException(
            "LoRa mesh requires a LoRa hardware radio adapter (e.g., Semtech SX1276/SX1262, RadioHead library). " +
            "Configure via MeshAdapterOptions and register a platform-specific IMeshNetwork implementation.");

    public Task SendMessageAsync(int destinationNodeId, byte[] payload, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException(
            "LoRa mesh requires a LoRa hardware radio adapter. " +
            "Configure via MeshAdapterOptions and register a platform-specific IMeshNetwork implementation.");

    public Task<MeshTopology> DiscoverTopologyAsync(CancellationToken ct = default) =>
        throw new PlatformNotSupportedException(
            "LoRa mesh requires a LoRa hardware radio adapter. " +
            "Configure via MeshAdapterOptions and register a platform-specific IMeshNetwork implementation.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
