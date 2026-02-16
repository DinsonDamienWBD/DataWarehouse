using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Deployment.EdgeProfiles;

/// <summary>
/// Raspberry Pi edge deployment profile preset.
/// Optimized for Raspberry Pi with 512MB-1GB RAM, SD card storage, WiFi/100Mbps Ethernet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Raspberry Pi Constraints:</b>
/// - Memory: 512MB-1GB RAM (256MB ceiling to leave room for OS)
/// - Storage: SD card (high write amplification, limited endurance)
/// - Network: WiFi (2.4/5GHz) or 100Mbps Ethernet
/// - Power: Potentially battery-powered (energy efficiency important)
/// </para>
/// <para>
/// <b>Profile Optimizations:</b>
/// - Memory ceiling: 256MB (conservative for 512MB+ systems)
/// - Essential plugins only: UltimateStorage, TamperProof, EdgeSensorMesh, UltimateCompression
/// - Flash optimized: Reduce SD card wear via fewer small writes
/// - Offline resilience: Buffer data when WiFi drops
/// - Bandwidth limit: 10MB/s (suitable for 100Mbps Ethernet or 802.11n WiFi)
/// - Max connections: 10 (conserve resources)
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Raspberry Pi edge profile preset (ENV-05)")]
public static class RaspberryPiProfile
{
    /// <summary>
    /// Creates a Raspberry Pi edge profile with conservative resource limits.
    /// </summary>
    /// <returns>Edge profile configured for Raspberry Pi.</returns>
    public static EdgeProfile Create() => new()
    {
        Name = "raspberry-pi",
        MaxMemoryBytes = 256 * 1024 * 1024, // 256MB (conservative for RPi with 512MB/1GB RAM)
        AllowedPlugins = new[]
        {
            "UltimateStorage",          // Core storage engine
            "TamperProof",              // Data integrity
            "EdgeSensorMesh",           // Edge/IoT sensor support (Phase 36)
            "UltimateCompression"       // Save bandwidth and storage space
        },
        FlashOptimized = true,              // Reduce SD card wear
        OfflineResilience = true,           // Buffer data when WiFi drops
        MaxConcurrentConnections = 10,      // Limit network connections
        BandwidthCeilingBytesPerSec = 10 * 1024 * 1024 // 10 MB/s (100Mbps Ethernet or WiFi)
    };
}
