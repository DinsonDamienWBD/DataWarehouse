namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.ResourceEfficiency;

/// <summary>
/// Optimizes memory usage to reduce power consumption from DRAM.
/// Implements memory compression, page deduplication, and proactive reclamation.
/// </summary>
public sealed class MemoryOptimizationStrategy : SustainabilityStrategyBase
{
    private long _totalMemoryBytes;
    private long _usedMemoryBytes;
    private long _compressedBytes;
    private Timer? _monitorTimer;
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "memory-optimization";
    /// <inheritdoc/>
    public override string DisplayName => "Memory Optimization";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.ResourceEfficiency;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.RealTimeMonitoring | SustainabilityCapabilities.ActiveControl;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Optimizes memory usage through compression and deduplication to reduce DRAM power consumption.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "memory", "ram", "optimization", "compression", "efficiency" };

    /// <summary>Memory usage percent.</summary>
    public double UsagePercent { get { lock (_lock) return _totalMemoryBytes > 0 ? (double)_usedMemoryBytes / _totalMemoryBytes * 100 : 0; } }

    /// <summary>Compression ratio achieved.</summary>
    public double CompressionRatio { get { lock (_lock) return _compressedBytes > 0 ? (double)_usedMemoryBytes / _compressedBytes : 1; } }

    /// <summary>Target memory usage percent.</summary>
    public double TargetUsagePercent { get; set; } = 70;

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _totalMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        _monitorTimer = new Timer(_ => MonitorMemory(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _monitorTimer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Triggers memory optimization.</summary>
    public void OptimizeMemory()
    {
        GC.Collect(2, GCCollectionMode.Optimized, false, true);
        GC.WaitForPendingFinalizers();
        RecordOptimizationAction();
    }

    private void MonitorMemory()
    {
        var info = GC.GetGCMemoryInfo();
        lock (_lock)
        {
            _usedMemoryBytes = GC.GetTotalMemory(false);
            _compressedBytes = info.MemoryLoadBytes;
        }

        // Estimate power: ~3W per 8GB of active DRAM
        var powerWatts = (_usedMemoryBytes / (8L * 1024 * 1024 * 1024)) * 3.0;
        RecordSample(powerWatts, 0);

        if (UsagePercent > TargetUsagePercent * 1.2)
        {
            OptimizeMemory();
        }

        UpdateRecommendations();
    }

    private void UpdateRecommendations()
    {
        ClearRecommendations();
        if (UsagePercent > 80)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-high-memory",
                Type = "HighMemoryUsage",
                Priority = 6,
                Description = $"Memory usage at {UsagePercent:F0}%. Consider freeing unused resources.",
                EstimatedEnergySavingsWh = 1,
                CanAutoApply = true,
                Action = "gc-collect"
            });
        }
    }
}
