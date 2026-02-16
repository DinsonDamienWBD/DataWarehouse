using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Edge.Mesh;

/// <summary>
/// Configuration for mesh network devices.
/// </summary>
/// <remarks>
/// Specifies mesh protocol, node role, network ID, and sleep scheduling for battery-powered IoT deployments.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Mesh network settings (EDGE-08)")]
public sealed record MeshSettings
{
    /// <summary>
    /// Mesh protocol to use. Default: LoRa.
    /// </summary>
    public MeshProtocol Protocol { get; init; } = MeshProtocol.LoRa;

    /// <summary>
    /// Node role in the mesh network. Default: EndDevice.
    /// </summary>
    public NodeRole Role { get; init; } = NodeRole.EndDevice;

    /// <summary>
    /// Hardware device path (e.g., /dev/ttyUSB0 for LoRa radio on Linux).
    /// Platform-specific. Null/empty for default device.
    /// </summary>
    public string? DevicePath { get; init; }

    /// <summary>
    /// Network ID for mesh isolation. Nodes with different IDs cannot communicate.
    /// Default: 1.
    /// </summary>
    public int NetworkId { get; init; } = 1;

    /// <summary>
    /// Whether sleep scheduling is enabled for battery-powered nodes. Default: false.
    /// </summary>
    public bool EnableSleepSchedule { get; init; } = false;

    /// <summary>
    /// Wake interval for sleep scheduling. Default: 1 minute.
    /// Node wakes to check for messages, then sleeps again.
    /// </summary>
    public TimeSpan WakeInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Sleep duration between wake intervals. Default: 9 minutes (10% duty cycle).
    /// </summary>
    public TimeSpan SleepDuration { get; init; } = TimeSpan.FromMinutes(9);
}

/// <summary>
/// Mesh network protocol.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Mesh protocol enumeration (EDGE-08)")]
public enum MeshProtocol
{
    /// <summary>
    /// Zigbee mesh (IEEE 802.15.4-based, 2.4GHz, 10-100m range).
    /// </summary>
    Zigbee,

    /// <summary>
    /// LoRa mesh (long-range, sub-GHz, 2-15km range depending on terrain).
    /// </summary>
    LoRa,

    /// <summary>
    /// BLE mesh (Bluetooth Low Energy mesh, 2.4GHz, 10-30m range).
    /// </summary>
    BLE
}

/// <summary>
/// Node role in mesh network.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Mesh node role enumeration (EDGE-08)")]
public enum NodeRole
{
    /// <summary>
    /// Coordinator: Forms and manages the mesh network. Always powered, routes messages.
    /// </summary>
    Coordinator,

    /// <summary>
    /// Router: Forwards messages between nodes. Always powered, extends network coverage.
    /// </summary>
    Router,

    /// <summary>
    /// End device: Leaf node, typically battery-powered. Sends/receives data but doesn't route.
    /// </summary>
    EndDevice,

    /// <summary>
    /// Gateway: Bridge between mesh network and external systems (e.g., Ethernet, Wi-Fi, cellular).
    /// </summary>
    Gateway
}
