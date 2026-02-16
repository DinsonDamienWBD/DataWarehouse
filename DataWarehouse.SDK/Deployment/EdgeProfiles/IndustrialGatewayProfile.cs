using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Deployment.EdgeProfiles;

/// <summary>
/// Industrial gateway edge deployment profile preset.
/// Optimized for industrial IoT gateways with 1GB RAM, industrial flash storage, gigabit Ethernet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Industrial Gateway Characteristics:</b>
/// - Memory: 1GB-2GB RAM (1GB ceiling with headroom for OS)
/// - Storage: Industrial-grade flash (eMMC, SLC NAND)
/// - Network: Gigabit Ethernet (wired, more reliable than WiFi)
/// - Protocols: MQTT, CoAP, Modbus, OPC UA (industrial protocols)
/// - Reliability: Higher than consumer edge (designed for 24/7 operation)
/// </para>
/// <para>
/// <b>Profile Optimizations:</b>
/// - Memory ceiling: 1GB (allows more plugins than Raspberry Pi)
/// - Allowed plugins: UltimateStorage, EdgeSensorMesh, MQTT/CoAP, TamperProof, UltimateCompression, DataGravityScheduler
/// - Flash optimized: Industrial flash more durable, but still benefit from reduced writes
/// - Offline resilience: Critical for industrial environments (network interruptions common)
/// - Bandwidth limit: 50MB/s (gigabit Ethernet common)
/// - Max connections: 100 (higher than Raspberry Pi, industrial gateways aggregate many sensors)
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Industrial gateway edge profile preset (ENV-05)")]
public static class IndustrialGatewayProfile
{
    /// <summary>
    /// Creates an industrial gateway edge profile with moderate resource limits.
    /// </summary>
    /// <returns>Edge profile configured for industrial IoT gateway.</returns>
    public static EdgeProfile Create() => new()
    {
        Name = "industrial-gateway",
        MaxMemoryBytes = 1024 * 1024 * 1024, // 1GB
        AllowedPlugins = new[]
        {
            "UltimateStorage",          // Core storage engine
            "EdgeSensorMesh",           // Sensor data aggregation
            "MqttIntegration",          // MQTT protocol support
            "CoapIntegration",          // CoAP protocol support
            "TamperProof",              // Industrial data integrity requirements
            "UltimateCompression",      // Bandwidth optimization
            "DataGravityScheduler"      // Data movement optimization for edge-to-cloud sync
        },
        FlashOptimized = true,              // Industrial flash storage still benefits
        OfflineResilience = true,           // Critical for industrial environments
        MaxConcurrentConnections = 100,     // Aggregate many sensors/actuators
        BandwidthCeilingBytesPerSec = 50 * 1024 * 1024 // 50 MB/s (gigabit Ethernet common)
    };
}
