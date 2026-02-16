using DataWarehouse.SDK.Contracts;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace DataWarehouse.SDK.Deployment.EdgeProfiles;

/// <summary>
/// Fluent builder for custom edge deployment profiles.
/// Allows creating tailored edge profiles for specific hardware/requirements.
/// </summary>
/// <remarks>
/// <para>
/// <b>Usage Example:</b>
/// <code>
/// var profile = new CustomEdgeProfileBuilder()
///     .WithName("custom-iot")
///     .WithMemoryCeilingMB(384)
///     .AllowPlugins("UltimateStorage", "TamperProof", "EdgeSensorMesh")
///     .WithMaxConnections(25)
///     .WithBandwidthCeilingMBps(15)
///     .Build();
/// </code>
/// </para>
/// <para>
/// Use presets (<see cref="RaspberryPiProfile"/>, <see cref="IndustrialGatewayProfile"/>) when possible.
/// Custom profiles are for non-standard edge hardware or specific optimization requirements.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Custom edge profile builder (ENV-05)")]
public sealed class CustomEdgeProfileBuilder
{
    private string _name = "custom";
    private long _maxMemoryBytes = 512 * 1024 * 1024; // Default 512MB
    private readonly List<string> _allowedPlugins = new();
    private bool _flashOptimized = true;
    private bool _offlineResilience = true;
    private int _maxConnections = 50;
    private long _bandwidthCeiling = 25 * 1024 * 1024; // Default 25 MB/s
    private readonly Dictionary<string, object> _customSettings = new();

    /// <summary>
    /// Sets the profile name.
    /// </summary>
    public CustomEdgeProfileBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    /// <summary>
    /// Sets the memory ceiling in bytes.
    /// </summary>
    public CustomEdgeProfileBuilder WithMemoryCeiling(long bytes)
    {
        _maxMemoryBytes = bytes;
        return this;
    }

    /// <summary>
    /// Sets the memory ceiling in megabytes (convenience method).
    /// </summary>
    public CustomEdgeProfileBuilder WithMemoryCeilingMB(int megabytes)
    {
        _maxMemoryBytes = megabytes * 1024L * 1024L;
        return this;
    }

    /// <summary>
    /// Adds a single allowed plugin.
    /// </summary>
    public CustomEdgeProfileBuilder AllowPlugin(string pluginName)
    {
        _allowedPlugins.Add(pluginName);
        return this;
    }

    /// <summary>
    /// Adds multiple allowed plugins.
    /// </summary>
    public CustomEdgeProfileBuilder AllowPlugins(params string[] pluginNames)
    {
        _allowedPlugins.AddRange(pluginNames);
        return this;
    }

    /// <summary>
    /// Enables or disables flash storage optimization.
    /// </summary>
    public CustomEdgeProfileBuilder WithFlashOptimization(bool enabled = true)
    {
        _flashOptimized = enabled;
        return this;
    }

    /// <summary>
    /// Enables or disables offline resilience (data buffering when network unavailable).
    /// </summary>
    public CustomEdgeProfileBuilder WithOfflineResilience(bool enabled = true)
    {
        _offlineResilience = enabled;
        return this;
    }

    /// <summary>
    /// Sets the maximum number of concurrent network connections.
    /// </summary>
    public CustomEdgeProfileBuilder WithMaxConnections(int maxConnections)
    {
        _maxConnections = maxConnections;
        return this;
    }

    /// <summary>
    /// Sets the network bandwidth ceiling in MB/s (convenience method).
    /// </summary>
    public CustomEdgeProfileBuilder WithBandwidthCeilingMBps(int megabytesPerSecond)
    {
        _bandwidthCeiling = megabytesPerSecond * 1024L * 1024L;
        return this;
    }

    /// <summary>
    /// Sets the network bandwidth ceiling in bytes/s.
    /// </summary>
    public CustomEdgeProfileBuilder WithBandwidthCeiling(long bytesPerSecond)
    {
        _bandwidthCeiling = bytesPerSecond;
        return this;
    }

    /// <summary>
    /// Adds a custom setting to the profile.
    /// </summary>
    public CustomEdgeProfileBuilder WithCustomSetting(string key, object value)
    {
        _customSettings[key] = value;
        return this;
    }

    /// <summary>
    /// Builds the edge profile with the configured settings.
    /// </summary>
    public EdgeProfile Build() => new()
    {
        Name = _name,
        MaxMemoryBytes = _maxMemoryBytes,
        AllowedPlugins = _allowedPlugins.ToArray(),
        FlashOptimized = _flashOptimized,
        OfflineResilience = _offlineResilience,
        MaxConcurrentConnections = _maxConnections,
        BandwidthCeilingBytesPerSec = _bandwidthCeiling,
        CustomSettings = _customSettings.ToImmutableDictionary()
    };
}
