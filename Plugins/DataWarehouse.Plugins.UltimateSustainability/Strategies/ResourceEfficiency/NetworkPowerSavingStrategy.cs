using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.ResourceEfficiency;

/// <summary>
/// Manages network interface power states including link speed reduction,
/// Wake-on-LAN, and interface power-down during idle periods.
/// </summary>
public sealed class NetworkPowerSavingStrategy : SustainabilityStrategyBase
{
    private readonly Dictionary<string, NetworkInterfaceState> _interfaces = new();
    private Timer? _monitorTimer;
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "network-power-saving";
    /// <inheritdoc/>
    public override string DisplayName => "Network Power Saving";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.ResourceEfficiency;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.ActiveControl | SustainabilityCapabilities.RealTimeMonitoring;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Manages network interface power states including link speed reduction and interface idle power-down.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "network", "ethernet", "power", "eee", "wol", "speed" };

    /// <summary>Enable Energy Efficient Ethernet.</summary>
    public bool EnableEee { get; set; } = true;

    /// <summary>Reduce link speed when idle.</summary>
    public bool ReduceIdleSpeed { get; set; } = true;

    /// <summary>Target idle speed (Mbps).</summary>
    public int IdleSpeedMbps { get; set; } = 100;

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        DiscoverInterfaces();
        _monitorTimer = new Timer(_ => MonitorInterfaces(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _monitorTimer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Sets interface to low power mode.</summary>
    public async Task SetLowPowerModeAsync(string interfaceId, bool enable, CancellationToken ct = default)
    {
        NetworkInterfaceState? iface;
        lock (_lock) { _interfaces.TryGetValue(interfaceId, out iface); }
        if (iface == null) return;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                if (enable)
                {
                    // Use ethtool to enable Energy Efficient Ethernet and reduce speed
                    if (EnableEee)
                    {
                        using var eeeProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "ethtool",
                            Arguments = $"--set-eee {interfaceId} eee on",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardError = true
                        });
                        if (eeeProc != null) await eeeProc.WaitForExitAsync(ct);
                    }

                    // Reduce link speed via ethtool
                    if (ReduceIdleSpeed)
                    {
                        using var speedProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "ethtool",
                            Arguments = $"-s {interfaceId} speed {IdleSpeedMbps} duplex full autoneg off",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardError = true
                        });
                        if (speedProc != null) await speedProc.WaitForExitAsync(ct);
                    }
                }
                else
                {
                    // Restore auto-negotiation
                    using var restoreProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "ethtool",
                        Arguments = $"-s {interfaceId} autoneg on",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true
                    });
                    if (restoreProc != null) await restoreProc.WaitForExitAsync(ct);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NetworkPowerSaving] ethtool failed for {interfaceId}: {ex.Message}");
            }
        }

        lock (_lock)
        {
            if (_interfaces.TryGetValue(interfaceId, out var i))
            {
                i.LowPowerMode = enable;
                if (enable)
                    i.CurrentSpeedMbps = IdleSpeedMbps;
                else if (i.MaxSpeedMbps > 0)
                    i.CurrentSpeedMbps = i.MaxSpeedMbps;
            }
        }

        if (enable) RecordEnergySaved(2); // ~2W savings per interface
        RecordOptimizationAction();
    }

    private void DiscoverInterfaces()
    {
        var discovered = new Dictionary<string, NetworkInterfaceState>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                var netClassPath = "/sys/class/net";
                if (Directory.Exists(netClassPath))
                {
                    foreach (var ifaceDir in Directory.GetDirectories(netClassPath))
                    {
                        var ifaceName = Path.GetFileName(ifaceDir);
                        // Skip loopback and virtual interfaces
                        if (ifaceName == "lo" || ifaceName.StartsWith("veth", StringComparison.Ordinal) ||
                            ifaceName.StartsWith("br-", StringComparison.Ordinal) ||
                            ifaceName.StartsWith("docker", StringComparison.Ordinal))
                            continue;

                        // Check if it's a physical network device (has a device link)
                        var devicePath = Path.Combine(ifaceDir, "device");
                        if (!Directory.Exists(devicePath) && !File.Exists(devicePath))
                            continue;

                        // Read current speed
                        var speedPath = Path.Combine(ifaceDir, "speed");
                        int speedMbps = 1000; // Default assumption
                        if (File.Exists(speedPath))
                        {
                            try
                            {
                                var speedStr = File.ReadAllText(speedPath).Trim();
                                if (int.TryParse(speedStr, out var s) && s > 0)
                                    speedMbps = s;
                            }
                            catch { }
                        }

                        discovered[ifaceName] = new NetworkInterfaceState
                        {
                            Id = ifaceName,
                            MaxSpeedMbps = speedMbps,
                            CurrentSpeedMbps = speedMbps
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NetworkPowerSaving] Interface discovery failed: {ex.Message}");
            }
        }
        else
        {
            // Non-Linux: discover via System.Net.NetworkInformation
            try
            {
                foreach (var iface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (iface.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    if (iface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;

                    var speedMbps = iface.Speed > 0 ? (int)(iface.Speed / 1_000_000) : 1000;
                    discovered[iface.Name] = new NetworkInterfaceState
                    {
                        Id = iface.Name,
                        MaxSpeedMbps = speedMbps,
                        CurrentSpeedMbps = speedMbps
                    };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NetworkPowerSaving] Interface discovery (non-Linux) failed: {ex.Message}");
            }
        }

        lock (_lock)
        {
            foreach (var kvp in discovered)
                _interfaces[kvp.Key] = kvp.Value;
        }
    }

    private void MonitorInterfaces()
    {
        List<string> idleInterfaces;
        lock (_lock)
        {
            foreach (var iface in _interfaces.Values)
                iface.IdleSeconds += 30;

            idleInterfaces = _interfaces.Values
                .Where(i => ReduceIdleSpeed && i.IdleSeconds > 60 && !i.LowPowerMode)
                .Select(i => i.Id)
                .ToList();
        }

        foreach (var id in idleInterfaces)
            _ = SetLowPowerModeAsync(id, true);

        UpdateRecommendations();
    }

    private void UpdateRecommendations()
    {
        ClearRecommendations();
        int highSpeedIdle;
        lock (_lock) { highSpeedIdle = _interfaces.Values.Count(i => i.CurrentSpeedMbps > IdleSpeedMbps && i.IdleSeconds > 60); }
        if (highSpeedIdle > 0)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-reduce-speed",
                Type = "ReduceSpeed",
                Priority = 4,
                Description = $"{highSpeedIdle} interface(s) idle at high speed. Consider reducing.",
                EstimatedEnergySavingsWh = highSpeedIdle * 2,
                CanAutoApply = true
            });
        }
    }
}

/// <summary>Network interface state.</summary>
public sealed class NetworkInterfaceState
{
    public required string Id { get; init; }
    public int MaxSpeedMbps { get; init; }
    public int CurrentSpeedMbps { get; set; }
    public bool LowPowerMode { get; set; }
    public int IdleSeconds { get; set; }
}
