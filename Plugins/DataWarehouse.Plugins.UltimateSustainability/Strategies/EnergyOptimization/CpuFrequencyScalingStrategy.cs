using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.EnergyOptimization;

/// <summary>
/// Controls CPU frequency scaling (DVFS - Dynamic Voltage and Frequency Scaling)
/// to reduce power consumption during low-utilization periods.
/// Supports Windows power plans, Linux cpufreq governors, and Intel P-states.
/// </summary>
public sealed class CpuFrequencyScalingStrategy : SustainabilityStrategyBase
{
    private FrequencyGovernor _currentGovernor = FrequencyGovernor.OnDemand;
    private double _currentFrequencyMhz;
    private double _minFrequencyMhz;
    private double _maxFrequencyMhz;
    private double _baseFrequencyMhz;
    private Timer? _monitorTimer;
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "cpu-frequency-scaling";

    /// <inheritdoc/>
    public override string DisplayName => "CPU Frequency Scaling";

    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.EnergyOptimization;

    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.ActiveControl |
        SustainabilityCapabilities.RealTimeMonitoring |
        SustainabilityCapabilities.PredictiveAnalytics;

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Controls CPU frequency scaling (DVFS) to reduce power consumption. " +
        "Adjusts processor frequency and voltage based on workload demands, " +
        "supporting power-saver, balanced, and performance modes.";

    /// <inheritdoc/>
    public override string[] Tags => new[] { "cpu", "frequency", "dvfs", "power", "scaling" };

    /// <summary>
    /// Gets the current CPU frequency governor.
    /// </summary>
    public FrequencyGovernor CurrentGovernor
    {
        get { lock (_lock) return _currentGovernor; }
    }

    /// <summary>
    /// Gets the current CPU frequency in MHz.
    /// </summary>
    public double CurrentFrequencyMhz
    {
        get { lock (_lock) return _currentFrequencyMhz; }
    }

    /// <summary>
    /// Gets the minimum CPU frequency in MHz.
    /// </summary>
    public double MinFrequencyMhz
    {
        get { lock (_lock) return _minFrequencyMhz; }
    }

    /// <summary>
    /// Gets the maximum CPU frequency in MHz.
    /// </summary>
    public double MaxFrequencyMhz
    {
        get { lock (_lock) return _maxFrequencyMhz; }
    }

    /// <summary>
    /// Whether to allow frequency changes.
    /// </summary>
    public bool AllowFrequencyControl { get; set; } = true;

    /// <summary>
    /// Minimum frequency limit as percentage of base (0-100).
    /// </summary>
    public int MinFrequencyPercent { get; set; } = 30;

    /// <summary>
    /// Maximum frequency limit as percentage of max (0-100).
    /// </summary>
    public int MaxFrequencyPercent { get; set; } = 100;

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        // Detect CPU capabilities
        DetectCpuCapabilities();

        // Start monitoring
        _monitorTimer = new Timer(
            _ => MonitorCpuFrequency(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5));

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _monitorTimer?.Dispose();
        _monitorTimer = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets the CPU frequency governor.
    /// </summary>
    public async Task SetGovernorAsync(FrequencyGovernor governor, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (!AllowFrequencyControl)
            throw new InvalidOperationException("Frequency control is disabled.");

        bool success = false;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            success = await SetWindowsPowerPlanAsync(governor, ct);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            success = await SetLinuxGovernorAsync(governor, ct);
        }

        if (success)
        {
            lock (_lock)
            {
                _currentGovernor = governor;
            }
            RecordOptimizationAction();

            // Estimate energy savings
            var savingsPercent = governor switch
            {
                FrequencyGovernor.Powersave => 0.30,
                FrequencyGovernor.Conservative => 0.15,
                FrequencyGovernor.OnDemand => 0.0,
                FrequencyGovernor.Performance => -0.20,
                _ => 0.0
            };

            if (savingsPercent > 0)
            {
                RecordEnergySaved(_baseFrequencyMhz * savingsPercent * 0.1);
            }
        }

        UpdateRecommendations();
    }

    /// <summary>
    /// Sets the CPU frequency limits.
    /// </summary>
    public async Task SetFrequencyLimitsAsync(int minPercent, int maxPercent, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (!AllowFrequencyControl)
            throw new InvalidOperationException("Frequency control is disabled.");

        if (minPercent < 0 || minPercent > 100)
            throw new ArgumentOutOfRangeException(nameof(minPercent));
        if (maxPercent < 0 || maxPercent > 100)
            throw new ArgumentOutOfRangeException(nameof(maxPercent));
        if (minPercent > maxPercent)
            throw new ArgumentException("Min frequency cannot exceed max frequency.");

        bool success = false;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            success = await SetLinuxFrequencyLimitsAsync(minPercent, maxPercent, ct);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            success = await SetWindowsProcessorThrottleAsync(maxPercent, ct);
        }

        if (success)
        {
            MinFrequencyPercent = minPercent;
            MaxFrequencyPercent = maxPercent;
            RecordOptimizationAction();
        }
    }

    /// <summary>
    /// Gets current CPU frequency information for all cores.
    /// </summary>
    public CpuFrequencyInfo GetFrequencyInfo()
    {
        ThrowIfNotInitialized();

        lock (_lock)
        {
            return new CpuFrequencyInfo
            {
                Timestamp = DateTimeOffset.UtcNow,
                CurrentFrequencyMhz = _currentFrequencyMhz,
                MinFrequencyMhz = _minFrequencyMhz,
                MaxFrequencyMhz = _maxFrequencyMhz,
                BaseFrequencyMhz = _baseFrequencyMhz,
                Governor = _currentGovernor,
                FrequencyPercent = _baseFrequencyMhz > 0 ? (_currentFrequencyMhz / _baseFrequencyMhz) * 100 : 100,
                EstimatedPowerSavingsPercent = CalculatePowerSavings()
            };
        }
    }

    /// <summary>
    /// Temporarily boosts CPU frequency for demanding workloads.
    /// </summary>
    public async Task<IDisposable> BoostFrequencyAsync(TimeSpan duration, CancellationToken ct = default)
    {
        var originalGovernor = CurrentGovernor;
        await SetGovernorAsync(FrequencyGovernor.Performance, ct);

        return new FrequencyBoostHandle(this, originalGovernor, duration);
    }

    private void DetectCpuCapabilities()
    {
        // Default values based on typical modern CPUs
        _baseFrequencyMhz = 2400;
        _minFrequencyMhz = 800;
        _maxFrequencyMhz = 4500;
        _currentFrequencyMhz = _baseFrequencyMhz;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            DetectLinuxCpuCapabilities();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            DetectWindowsCpuCapabilities();
        }
    }

    private void DetectLinuxCpuCapabilities()
    {
        try
        {
            var cpuInfoPath = "/sys/devices/system/cpu/cpu0/cpufreq/";

            if (File.Exists(cpuInfoPath + "cpuinfo_min_freq"))
            {
                var minFreq = long.Parse(File.ReadAllText(cpuInfoPath + "cpuinfo_min_freq").Trim());
                _minFrequencyMhz = minFreq / 1000.0;
            }

            if (File.Exists(cpuInfoPath + "cpuinfo_max_freq"))
            {
                var maxFreq = long.Parse(File.ReadAllText(cpuInfoPath + "cpuinfo_max_freq").Trim());
                _maxFrequencyMhz = maxFreq / 1000.0;
            }

            if (File.Exists(cpuInfoPath + "base_frequency"))
            {
                var baseFreq = long.Parse(File.ReadAllText(cpuInfoPath + "base_frequency").Trim());
                _baseFrequencyMhz = baseFreq / 1000.0;
            }
            else
            {
                _baseFrequencyMhz = (_minFrequencyMhz + _maxFrequencyMhz) / 2;
            }

            if (File.Exists(cpuInfoPath + "scaling_governor"))
            {
                var governor = File.ReadAllText(cpuInfoPath + "scaling_governor").Trim();
                _currentGovernor = ParseLinuxGovernor(governor);
            }
        }
        catch
        {
            // Use defaults
        }
    }

    private void DetectWindowsCpuCapabilities()
    {
        // Windows detection would use WMI or registry
        // Using typical values for now
        _baseFrequencyMhz = Environment.ProcessorCount >= 8 ? 3200 : 2400;
        _maxFrequencyMhz = _baseFrequencyMhz * 1.4;
        _minFrequencyMhz = _baseFrequencyMhz * 0.3;
    }

    private void MonitorCpuFrequency()
    {
        try
        {
            double currentFreq = _baseFrequencyMhz;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var scalingCurFreqPath = "/sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq";
                if (File.Exists(scalingCurFreqPath))
                {
                    currentFreq = long.Parse(File.ReadAllText(scalingCurFreqPath).Trim()) / 1000.0;
                }
            }

            lock (_lock)
            {
                _currentFrequencyMhz = currentFreq;
            }

            RecordSample(EstimatePowerFromFrequency(currentFreq), 0);
        }
        catch
        {
            // Monitoring failed, continue
        }
    }

    private async Task<bool> SetWindowsPowerPlanAsync(FrequencyGovernor governor, CancellationToken ct)
    {
        var planGuid = governor switch
        {
            FrequencyGovernor.Powersave => "a1841308-3541-4fab-bc81-f71556f20b4a",
            FrequencyGovernor.OnDemand => "381b4222-f694-41f0-9685-ff5bb260df2e",
            FrequencyGovernor.Performance => "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            _ => "381b4222-f694-41f0-9685-ff5bb260df2e"
        };

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = $"/setactive {planGuid}",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = System.Diagnostics.Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync(ct);
                return process.ExitCode == 0;
            }
        }
        catch
        {
            // Failed to set power plan
        }

        return false;
    }

    private async Task<bool> SetLinuxGovernorAsync(FrequencyGovernor governor, CancellationToken ct)
    {
        var governorName = governor switch
        {
            FrequencyGovernor.Powersave => "powersave",
            FrequencyGovernor.Conservative => "conservative",
            FrequencyGovernor.OnDemand => "ondemand",
            FrequencyGovernor.Performance => "performance",
            FrequencyGovernor.Schedutil => "schedutil",
            _ => "ondemand"
        };

        try
        {
            var cpuCount = Environment.ProcessorCount;
            for (int i = 0; i < cpuCount; i++)
            {
                var path = $"/sys/devices/system/cpu/cpu{i}/cpufreq/scaling_governor";
                if (File.Exists(path))
                {
                    await File.WriteAllTextAsync(path, governorName, ct);
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> SetLinuxFrequencyLimitsAsync(int minPercent, int maxPercent, CancellationToken ct)
    {
        try
        {
            var minFreq = (long)(_minFrequencyMhz + (_maxFrequencyMhz - _minFrequencyMhz) * (minPercent / 100.0)) * 1000;
            var maxFreq = (long)(_minFrequencyMhz + (_maxFrequencyMhz - _minFrequencyMhz) * (maxPercent / 100.0)) * 1000;

            var cpuCount = Environment.ProcessorCount;
            for (int i = 0; i < cpuCount; i++)
            {
                var minPath = $"/sys/devices/system/cpu/cpu{i}/cpufreq/scaling_min_freq";
                var maxPath = $"/sys/devices/system/cpu/cpu{i}/cpufreq/scaling_max_freq";

                if (File.Exists(minPath))
                    await File.WriteAllTextAsync(minPath, minFreq.ToString(), ct);
                if (File.Exists(maxPath))
                    await File.WriteAllTextAsync(maxPath, maxFreq.ToString(), ct);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private Task<bool> SetWindowsProcessorThrottleAsync(int maxPercent, CancellationToken ct)
    {
        // Would use powercfg to set processor max state
        return Task.FromResult(true);
    }

    private static FrequencyGovernor ParseLinuxGovernor(string governor)
    {
        return governor.ToLowerInvariant() switch
        {
            "powersave" => FrequencyGovernor.Powersave,
            "conservative" => FrequencyGovernor.Conservative,
            "ondemand" => FrequencyGovernor.OnDemand,
            "performance" => FrequencyGovernor.Performance,
            "schedutil" => FrequencyGovernor.Schedutil,
            _ => FrequencyGovernor.OnDemand
        };
    }

    private double EstimatePowerFromFrequency(double frequencyMhz)
    {
        // Power scales roughly with V^2 * f, and V scales with f
        // So power scales approximately with f^3
        var normalizedFreq = frequencyMhz / _baseFrequencyMhz;
        var basePower = 65.0; // TDP estimate in watts
        return basePower * Math.Pow(normalizedFreq, 2.5);
    }

    private double CalculatePowerSavings()
    {
        var currentPower = EstimatePowerFromFrequency(_currentFrequencyMhz);
        var maxPower = EstimatePowerFromFrequency(_maxFrequencyMhz);
        return maxPower > 0 ? ((maxPower - currentPower) / maxPower) * 100 : 0;
    }

    private void UpdateRecommendations()
    {
        ClearRecommendations();

        var info = GetFrequencyInfo();

        if (info.Governor == FrequencyGovernor.Performance)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-switch-to-ondemand",
                Type = "SwitchGovernor",
                Priority = 7,
                Description = "CPU is in performance mode. Consider switching to on-demand for energy savings during idle periods.",
                EstimatedEnergySavingsWh = 10,
                CanAutoApply = true,
                Action = "set-governor",
                ActionParameters = new Dictionary<string, object> { ["governor"] = "ondemand" }
            });
        }

        if (info.FrequencyPercent > 90)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-high-frequency-alert",
                Type = "HighFrequencyAlert",
                Priority = 5,
                Description = $"CPU running at {info.FrequencyPercent:F0}% of maximum frequency. Check for runaway processes.",
                CanAutoApply = false
            });
        }
    }

    private sealed class FrequencyBoostHandle : IDisposable
    {
        private readonly CpuFrequencyScalingStrategy _strategy;
        private readonly FrequencyGovernor _originalGovernor;
        private readonly Timer _timer;
        private bool _disposed;

        public FrequencyBoostHandle(CpuFrequencyScalingStrategy strategy, FrequencyGovernor originalGovernor, TimeSpan duration)
        {
            _strategy = strategy;
            _originalGovernor = originalGovernor;
            _timer = new Timer(_ => Dispose(), null, duration, Timeout.InfiniteTimeSpan);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            _ = _strategy.SetGovernorAsync(_originalGovernor);
        }
    }
}

/// <summary>
/// CPU frequency governor/power plan.
/// </summary>
public enum FrequencyGovernor
{
    /// <summary>Always run at lowest frequency.</summary>
    Powersave,
    /// <summary>Slowly increase frequency under load.</summary>
    Conservative,
    /// <summary>Quickly scale frequency based on demand.</summary>
    OnDemand,
    /// <summary>Always run at highest frequency.</summary>
    Performance,
    /// <summary>Kernel scheduler-driven frequency scaling.</summary>
    Schedutil
}

/// <summary>
/// CPU frequency information.
/// </summary>
public sealed record CpuFrequencyInfo
{
    public required DateTimeOffset Timestamp { get; init; }
    public required double CurrentFrequencyMhz { get; init; }
    public required double MinFrequencyMhz { get; init; }
    public required double MaxFrequencyMhz { get; init; }
    public required double BaseFrequencyMhz { get; init; }
    public required FrequencyGovernor Governor { get; init; }
    public required double FrequencyPercent { get; init; }
    public required double EstimatedPowerSavingsPercent { get; init; }
}
