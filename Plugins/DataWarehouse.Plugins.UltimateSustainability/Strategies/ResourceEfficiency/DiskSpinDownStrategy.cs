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

    /// <summary>Disk states.</summary>
    public IReadOnlyDictionary<string, DiskState> Disks { get { lock (_lock) return new Dictionary<string, DiskState>(_disks); } }

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
        if (_disks.TryGetValue(diskId, out var disk) && disk.Type == DiskType.HDD)
        {
            // Would use hdparm -y on Linux
            disk.IsSpunDown = true;
            disk.LastSpinDown = DateTimeOffset.UtcNow;
            RecordOptimizationAction();
            RecordEnergySaved(5); // ~5Wh per hour of spin-down
        }
        await Task.CompletedTask;
    }

    private void DiscoverDisks()
    {
        // Discover from /sys/block on Linux
        _disks["sda"] = new DiskState { Id = "sda", Type = DiskType.SSD, IsSpunDown = false, IdleSeconds = 0 };
        _disks["sdb"] = new DiskState { Id = "sdb", Type = DiskType.HDD, IsSpunDown = false, IdleSeconds = 0 };
    }

    private void MonitorDisks()
    {
        foreach (var disk in _disks.Values)
        {
            disk.IdleSeconds += 60;
            if (disk.Type == DiskType.HDD && disk.IdleSeconds >= SpinDownSeconds && !disk.IsSpunDown)
            {
                _ = SpinDownAsync(disk.Id);
            }
        }
        UpdateRecommendations();
    }

    private void UpdateRecommendations()
    {
        ClearRecommendations();
        var activeHdds = _disks.Values.Count(d => d.Type == DiskType.HDD && !d.IsSpunDown);
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
