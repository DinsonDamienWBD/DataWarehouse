using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.Metrics;

/// <summary>
/// Tracks energy consumption across system components using RAPL, smart plugs,
/// and estimation models. Provides detailed energy breakdowns and trending.
/// </summary>
public sealed class EnergyConsumptionTrackingStrategy : SustainabilityStrategyBase
{
    private readonly ConcurrentQueue<EnergyDataPoint> _history = new();
    private double _totalEnergyWh;
    private double _currentPowerWatts;
    private Timer? _samplingTimer;
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "energy-consumption-tracking";
    /// <inheritdoc/>
    public override string DisplayName => "Energy Consumption Tracking";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.Metrics;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.RealTimeMonitoring | SustainabilityCapabilities.Reporting;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Tracks energy consumption using RAPL, smart plugs, and estimation. Provides breakdowns and trending.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "energy", "tracking", "rapl", "consumption", "power", "metrics" };

    /// <summary>Sampling interval.</summary>
    public TimeSpan SamplingInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Total energy consumed (Wh).</summary>
    public double TotalEnergyWh { get { lock (_lock) return _totalEnergyWh; } }

    /// <summary>Current power (W).</summary>
    public double CurrentPowerWatts { get { lock (_lock) return _currentPowerWatts; } }

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _samplingTimer = new Timer(_ => SampleEnergy(), null, TimeSpan.Zero, SamplingInterval);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _samplingTimer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Gets energy history.</summary>
    public IReadOnlyList<EnergyDataPoint> GetHistory(TimeSpan? duration = null)
    {
        var cutoff = duration.HasValue ? DateTimeOffset.UtcNow - duration.Value : DateTimeOffset.MinValue;
        return _history.Where(p => p.Timestamp >= cutoff).ToList().AsReadOnly();
    }

    /// <summary>Gets energy summary for a period.</summary>
    public EnergySummary GetSummary(TimeSpan period)
    {
        var history = GetHistory(period);
        if (!history.Any())
            return new EnergySummary { Period = period, TotalEnergyWh = 0 };

        return new EnergySummary
        {
            Period = period,
            TotalEnergyWh = history.Sum(h => h.EnergyWh),
            AveragePowerWatts = history.Average(h => h.PowerWatts),
            PeakPowerWatts = history.Max(h => h.PowerWatts),
            MinPowerWatts = history.Min(h => h.PowerWatts),
            SampleCount = history.Count
        };
    }

    // Track previous RAPL reading for delta calculation
    private long _lastRaplEnergyUj;
    private DateTimeOffset _lastRaplReadTime = DateTimeOffset.MinValue;

    private void SampleEnergy()
    {
        var (power, source) = ReadCurrentPower();
        var energyWh = power * SamplingInterval.TotalHours;

        lock (_lock)
        {
            _currentPowerWatts = power;
            _totalEnergyWh += energyWh;
        }

        var dataPoint = new EnergyDataPoint
        {
            Timestamp = DateTimeOffset.UtcNow,
            PowerWatts = power,
            EnergyWh = energyWh,
            Source = source
        };

        _history.Enqueue(dataPoint);
        while (_history.Count > 10000) _history.TryDequeue(out _);

        RecordSample(power, 0);
    }

    private (double PowerWatts, string Source) ReadCurrentPower()
    {
        // Priority 1: Intel RAPL on Linux (most accurate)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                var energyPath = "/sys/class/powercap/intel-rapl/intel-rapl:0/energy_uj";
                if (File.Exists(energyPath))
                {
                    var energyUj = long.Parse(File.ReadAllText(energyPath).Trim());
                    var now = DateTimeOffset.UtcNow;

                    if (_lastRaplReadTime != DateTimeOffset.MinValue)
                    {
                        var deltaUj = energyUj - _lastRaplEnergyUj;
                        // Handle RAPL counter wrap-around
                        if (deltaUj < 0)
                        {
                            var maxEnergyPath = "/sys/class/powercap/intel-rapl/intel-rapl:0/max_energy_range_uj";
                            if (File.Exists(maxEnergyPath) && long.TryParse(File.ReadAllText(maxEnergyPath).Trim(), out var maxUj))
                                deltaUj += maxUj;
                            else
                                deltaUj = 0;
                        }
                        var deltaSec = (now - _lastRaplReadTime).TotalSeconds;
                        if (deltaSec > 0)
                        {
                            _lastRaplEnergyUj = energyUj;
                            _lastRaplReadTime = now;
                            return (deltaUj / 1_000_000.0 / deltaSec, "RAPL");
                        }
                    }

                    _lastRaplEnergyUj = energyUj;
                    _lastRaplReadTime = now;
                }
            }
            catch { /* Fall through to estimation */ }
        }

        // Priority 2: Estimate from real CPU load using Process.TotalProcessorTime
        try
        {
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            var cpuTime1 = proc.TotalProcessorTime;
            var wall1 = DateTimeOffset.UtcNow;
            System.Threading.Thread.Sleep(100);
            proc.Refresh();
            var cpuTime2 = proc.TotalProcessorTime;
            var wall2 = DateTimeOffset.UtcNow;

            var cpuFraction = (cpuTime2 - cpuTime1).TotalSeconds /
                              ((wall2 - wall1).TotalSeconds * Environment.ProcessorCount);
            cpuFraction = Math.Max(0, Math.Min(1, cpuFraction));

            // Estimate from process-count-scaled TDP: idle ~15%, max load ~100%
            var tdp = Environment.ProcessorCount switch
            {
                <= 4 => 65.0,
                <= 8 => 95.0,
                <= 16 => 125.0,
                _ => 180.0
            };
            var estimatedPower = tdp * 0.15 + tdp * 0.85 * cpuFraction;
            return (estimatedPower, "CPUTimeEstimation");
        }
        catch { }

        // Fallback: conservative idle estimate
        return (30.0 + Environment.ProcessorCount * 2.0, "StaticEstimation");
    }
}

/// <summary>Energy data point.</summary>
public sealed record EnergyDataPoint
{
    public required DateTimeOffset Timestamp { get; init; }
    public required double PowerWatts { get; init; }
    public required double EnergyWh { get; init; }
    public required string Source { get; init; }
}

/// <summary>Energy summary for a period.</summary>
public sealed record EnergySummary
{
    public required TimeSpan Period { get; init; }
    public required double TotalEnergyWh { get; init; }
    public double AveragePowerWatts { get; init; }
    public double PeakPowerWatts { get; init; }
    public double MinPowerWatts { get; init; }
    public int SampleCount { get; init; }
}
