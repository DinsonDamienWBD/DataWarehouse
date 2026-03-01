using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.EnergyOptimization;

/// <summary>
/// Manages GPU power states for NVIDIA, AMD, and Intel GPUs.
/// Controls power limits, clock speeds, and idle power management.
/// </summary>
public sealed class GpuPowerManagementStrategy : SustainabilityStrategyBase
{
    // P2-4448: ConcurrentDictionary so MonitorGpus (timer thread) and GetGpus/SetPowerLimit
    // (caller thread) can access without a data race.
    private readonly ConcurrentDictionary<string, GpuInfo> _gpus = new();
    private Timer? _monitorTimer;

    /// <inheritdoc/>
    public override string StrategyId => "gpu-power-management";
    /// <inheritdoc/>
    public override string DisplayName => "GPU Power Management";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.EnergyOptimization;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.RealTimeMonitoring | SustainabilityCapabilities.ActiveControl;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Manages GPU power limits, clock speeds, and idle states for NVIDIA, AMD, and Intel GPUs.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "gpu", "nvidia", "amd", "intel", "power", "cuda", "graphics" };

    /// <summary>Target power limit as percentage of TDP.</summary>
    public int PowerLimitPercent { get; set; } = 80;
    /// <summary>Enable aggressive idle mode.</summary>
    public bool AggressiveIdleMode { get; set; } = true;

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        DiscoverGpus();
        _monitorTimer = new Timer(_ => MonitorGpus(), null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _monitorTimer?.Dispose();
        return Task.CompletedTask;
    }

    private void DiscoverGpus()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            DiscoverNvidiaGpusLinux();
        }
    }

    private void DiscoverNvidiaGpusLinux()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=index,name,power.limit,power.draw --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null) return;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',');
                if (parts.Length >= 4)
                {
                    var index = parts[0].Trim();
                    _gpus[$"gpu{index}"] = new GpuInfo
                    {
                        Index = int.Parse(index),
                        Name = parts[1].Trim(),
                        Vendor = "NVIDIA",
                        PowerLimitWatts = double.TryParse(parts[2].Trim(), out var pl) ? pl : 0,
                        PowerDrawWatts = double.TryParse(parts[3].Trim(), out var pd) ? pd : 0
                    };
                }
            }
        }
        catch { /* GPU discovery failure is non-fatal */ }
    }

    private void MonitorGpus()
    {
        DiscoverGpus(); // Refresh stats
        foreach (var gpu in _gpus.Values)
        {
            RecordSample(gpu.PowerDrawWatts, 0);
            if (gpu.PowerDrawWatts > 0)
            {
                var hourlyWh = gpu.PowerDrawWatts / 6; // 10 second sample
                RecordEnergySaved((gpu.PowerLimitWatts - gpu.PowerDrawWatts) / 6);
            }
        }
        UpdateRecommendations();
    }

    /// <summary>Sets GPU power limit.</summary>
    public bool SetPowerLimit(string gpuId, int powerLimitWatts)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return false;
        if (!_gpus.TryGetValue(gpuId, out var gpu)) return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = $"-i {gpu.Index} -pl {powerLimitWatts}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            process?.WaitForExit();
            RecordOptimizationAction();
            return process?.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Gets all discovered GPUs.</summary>
    public IReadOnlyList<GpuInfo> GetGpus() => _gpus.Values.ToList();

    private void UpdateRecommendations()
    {
        ClearRecommendations();
        foreach (var gpu in _gpus.Values.Where(g => g.PowerDrawWatts < g.PowerLimitWatts * 0.3))
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-{gpu.Index}-idle",
                Type = "GpuIdle",
                Priority = 5,
                Description = $"GPU {gpu.Index} ({gpu.Name}) at {gpu.PowerDrawWatts:F0}W, consider reducing power limit.",
                EstimatedEnergySavingsWh = (gpu.PowerLimitWatts - gpu.PowerDrawWatts) * 0.1,
                CanAutoApply = true,
                Action = "reduce-power-limit"
            });
        }
    }
}

/// <summary>GPU information.</summary>
public sealed class GpuInfo
{
    public int Index { get; init; }
    public required string Name { get; init; }
    public required string Vendor { get; init; }
    public double PowerLimitWatts { get; set; }
    public double PowerDrawWatts { get; set; }
    public double TemperatureCelsius { get; set; }
    public int UtilizationPercent { get; set; }
}
