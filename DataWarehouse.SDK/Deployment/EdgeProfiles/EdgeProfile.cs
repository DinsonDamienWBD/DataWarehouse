using DataWarehouse.SDK.Contracts;
using System.Collections.Immutable;

namespace DataWarehouse.SDK.Deployment.EdgeProfiles;

/// <summary>
/// Edge deployment profile defining resource constraints and optimizations for edge/IoT devices.
/// </summary>
/// <remarks>
/// <para>
/// Edge devices (Raspberry Pi, industrial gateways, IoT controllers) have severe resource constraints:
/// - Limited memory (256MB-1GB)
/// - Flash storage (SD cards, eMMC)
/// - Bandwidth constraints (WiFi, cellular)
/// - Power constraints (battery-powered)
/// </para>
/// <para>
/// Edge profiles configure DataWarehouse to operate within these limits:
/// - <see cref="MaxMemoryBytes"/>: Hard memory ceiling enforced via GC (.NET 9+)
/// - <see cref="AllowedPlugins"/>: Essential plugins only (disable non-critical features)
/// - <see cref="FlashOptimized"/>: Reduce write amplification for flash storage
/// - <see cref="OfflineResilience"/>: Buffer data when network unavailable
/// - <see cref="BandwidthCeilingBytesPerSec"/>: Throttle network usage
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Edge deployment profile (ENV-05)")]
public sealed record EdgeProfile
{
    /// <summary>Gets the profile name for identification.</summary>
    public string Name { get; init; } = "custom";

    /// <summary>
    /// Gets the hard memory ceiling in bytes.
    /// Enforced via <c>GC.RegisterMemoryLimit</c> (.NET 9+ only).
    /// </summary>
    /// <remarks>
    /// Examples: 256MB (Raspberry Pi), 1GB (industrial gateway).
    /// When memory limit reached, GC becomes aggressive to stay within ceiling.
    /// </remarks>
    public long MaxMemoryBytes { get; init; }

    /// <summary>
    /// Gets the list of allowed plugin names.
    /// All other plugins are disabled to conserve resources.
    /// </summary>
    /// <remarks>
    /// Essential plugins for edge:
    /// - UltimateStorage: Core storage engine
    /// - TamperProof: Data integrity
    /// - EdgeSensorMesh: Edge/IoT sensor support
    /// - UltimateCompression: Reduce bandwidth/storage
    /// </remarks>
    public IReadOnlyList<string> AllowedPlugins { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Gets whether flash storage optimization is enabled.
    /// Reduces write amplification to extend SD card/eMMC lifespan.
    /// </summary>
    /// <remarks>
    /// Optimizations:
    /// - Larger block cache (fewer small writes)
    /// - WAL sync mode = Periodic (every 5s) instead of FSync (every write)
    /// - Disable OS write-behind caching (use direct I/O)
    /// </remarks>
    public bool FlashOptimized { get; init; } = true;

    /// <summary>
    /// Gets whether offline resilience is enabled.
    /// Buffers data locally when network is unavailable (common for edge).
    /// </summary>
    /// <remarks>
    /// Offline buffer size: 100MB (configurable).
    /// Retry interval: 30s.
    /// Offline mode threshold: 3 consecutive network failures.
    /// </remarks>
    public bool OfflineResilience { get; init; } = true;

    /// <summary>
    /// Gets the maximum number of concurrent network connections.
    /// Limits resource usage for constrained edge devices.
    /// </summary>
    public int MaxConcurrentConnections { get; init; } = 10;

    /// <summary>
    /// Gets the network bandwidth ceiling in bytes per second.
    /// Throttles outbound traffic to avoid saturating limited connections (WiFi, cellular).
    /// </summary>
    /// <remarks>
    /// Examples: 10MB/s (Raspberry Pi WiFi), 50MB/s (industrial gateway gigabit).
    /// Uses token bucket algorithm for smooth throttling.
    /// </remarks>
    public long BandwidthCeilingBytesPerSec { get; init; } = 10 * 1024 * 1024; // 10 MB/s default

    /// <summary>
    /// Gets custom settings for profile-specific configuration.
    /// </summary>
    public IReadOnlyDictionary<string, object> CustomSettings { get; init; } = ImmutableDictionary<string, object>.Empty;
}
