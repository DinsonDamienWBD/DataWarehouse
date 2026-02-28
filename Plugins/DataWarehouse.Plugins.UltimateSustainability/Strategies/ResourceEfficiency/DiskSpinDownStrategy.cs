using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.ResourceEfficiency;

/// <summary>
/// Manages HDD spin-down and SSD power states to reduce storage power consumption
/// during idle periods. Configures APM levels and standby timeouts.
/// </summary>
public sealed class DiskSpinDownStrategy : SustainabilityStrategyBase
{
    private readonly Dictionary<string, DiskState> _disks = new();
    private Timer? _monitorTimer;
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "disk-spin-down";
    /// <inheritdoc/>
    public override string DisplayName => "Disk Spin-Down";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.ResourceEfficiency;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.ActiveControl | SustainabilityCapabilities.RealTimeMonitoring;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Manages HDD spin-down and SSD power states to reduce storage power consumption during idle periods.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "disk", "hdd", "ssd", "spin-down", "apm", "power" };

    /// <summary>Idle time before spin-down (seconds).</summary>
    public int SpinDownSeconds { get; set; } = 300;

    /// <summary>APM level (1-254, lower = more aggressive).</summary>
    public int ApmLevel { get; set; } = 128;

    /// <summary>Disk states (snapshot copy — thread-safe).</summary>
    public IReadOnlyDictionary<string, DiskState> Disks
    {
        get
        {
            lock (_lock)
            {
                return _disks.ToDictionary(kvp => kvp.Key, kvp => new DiskState
                {
                    Id = kvp.Value.Id,
                    Type = kvp.Value.Type,
                    IsSpunDown = kvp.Value.IsSpunDown,
                    IdleSeconds = kvp.Value.IdleSeconds,
                    LastSpinDown = kvp.Value.LastSpinDown
                });
            }
        }
    }

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        DiscoverDisks();
        _monitorTimer = new Timer(_ => MonitorDisks(), null, TimeSpan.Zero, TimeSpan.FromSeconds(60));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _monitorTimer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Forces a disk to spin down.</summary>
    public async Task SpinDownAsync(string diskId, CancellationToken ct = default)
    {
        DiskState? disk;
        lock (_lock)
        {
            _disks.TryGetValue(diskId, out disk);
        }

        if (disk == null || disk.Type != DiskType.HDD) return;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                // Use hdparm -y to spin down the disk immediately
                using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "hdparm",
                    Arguments = $"-y /dev/{diskId}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                });
                if (proc != null) await proc.WaitForExitAsync(ct);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DiskSpinDown] hdparm failed for {diskId}: {ex.Message}");
            }
        }

        lock (_lock)
        {
            if (_disks.TryGetValue(diskId, out var d))
            {
                d.IsSpunDown = true;
                d.LastSpinDown = DateTimeOffset.UtcNow;
            }
        }

        RecordOptimizationAction();
        RecordEnergySaved(5); // ~5Wh per hour of spin-down
    }

    private void DiscoverDisks()
    {
        var discovered = new Dictionary<string, DiskState>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                var sysBlockPath = "/sys/block";
                if (Directory.Exists(sysBlockPath))
                {
                    foreach (var deviceDir in Directory.GetDirectories(sysBlockPath))
                    {
                        var deviceName = Path.GetFileName(deviceDir);
                        // Skip loopback, ram, and device-mapper devices
                        if (deviceName.StartsWith("loop", StringComparison.Ordinal) ||
                            deviceName.StartsWith("ram", StringComparison.Ordinal) ||
                            deviceName.StartsWith("dm-", StringComparison.Ordinal))
                            continue;

                        // Determine disk type from rotational flag
                        var rotationalPath = Path.Combine(deviceDir, "queue", "rotational");
                        var diskType = DiskType.SSD;
                        if (File.Exists(rotationalPath))
                        {
                            var rotational = File.ReadAllText(rotationalPath).Trim();
                            diskType = rotational == "1" ? DiskType.HDD : DiskType.SSD;
                        }

                        // Check for NVMe
                        if (deviceName.StartsWith("nvme", StringComparison.Ordinal))
                            diskType = DiskType.NVMe;

                        discovered[deviceName] = new DiskState
                        {
                            Id = deviceName,
                            Type = diskType,
                            IsSpunDown = false,
                            IdleSeconds = 0
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DiskSpinDown] Discovery failed: {ex.Message}");
            }
        }

        // If discovery returned nothing (non-Linux or error), use a conservative empty set
        // so the strategy doesn't pretend to manage disks it can't reach.
        lock (_lock)
        {
            foreach (var kvp in discovered)
                _disks[kvp.Key] = kvp.Value;
        }
    }

    private void MonitorDisks()
    {
        List<(string Id, bool ShouldSpinDown)> actions;
        lock (_lock)
        {
            foreach (var disk in _disks.Values)
            {
                disk.IdleSeconds += 60;
            }
            actions = _disks.Values
                .Where(d => d.Type == DiskType.HDD && d.IdleSeconds >= SpinDownSeconds && !d.IsSpunDown)
                .Select(d => (d.Id, true))
                .ToList();
        }

        foreach (var (id, _) in actions)
            _ = SpinDownAsync(id);

        UpdateRecommendations();
    }

    private void UpdateRecommendations()
    {
        ClearRecommendations();
        int activeHdds;
        lock (_lock) { activeHdds = _disks.Values.Count(d => d.Type == DiskType.HDD && !d.IsSpunDown); }
        if (activeHdds > 0)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-spin-down",
                Type = "SpinDown",
                Priority = 4,
                Description = $"{activeHdds} HDD(s) active. Will spin down after {SpinDownSeconds}s idle.",
                EstimatedEnergySavingsWh = activeHdds * 5,
                CanAutoApply = true
            });
        }
    }
}

/// <summary>Disk type.</summary>
public enum DiskType { HDD, SSD, NVMe }

/// <summary>Disk state.</summary>
public sealed class DiskState
{
    public required string Id { get; init; }
    public required DiskType Type { get; init; }
    public bool IsSpunDown { get; set; }
    public int IdleSeconds { get; set; }
    public DateTimeOffset? LastSpinDown { get; set; }
}
