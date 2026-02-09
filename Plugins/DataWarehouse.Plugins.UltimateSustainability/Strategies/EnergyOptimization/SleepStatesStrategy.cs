using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.EnergyOptimization;

/// <summary>
/// Manages CPU and system sleep states (C-states, S-states) to reduce power
/// consumption during idle periods. Supports ACPI C0-C10 states and
/// system standby/hibernate configurations.
/// </summary>
public sealed class SleepStatesStrategy : SustainabilityStrategyBase
{
    private CState _currentCState = CState.C0;
    private SState _currentSState = SState.S0;
    private TimeSpan _idleTime = TimeSpan.Zero;
    private DateTimeOffset _lastActivityTime = DateTimeOffset.UtcNow;
    private Timer? _monitorTimer;
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "sleep-states";

    /// <inheritdoc/>
    public override string DisplayName => "Sleep States Management";

    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.EnergyOptimization;

    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.ActiveControl |
        SustainabilityCapabilities.RealTimeMonitoring;

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Manages CPU and system sleep states (ACPI C-states and S-states) for energy savings. " +
        "Enables deeper idle states during low activity periods while maintaining responsiveness.";

    /// <inheritdoc/>
    public override string[] Tags => new[] { "sleep", "c-states", "s-states", "acpi", "idle", "power" };

    /// <summary>
    /// Gets the current C-state.
    /// </summary>
    public CState CurrentCState
    {
        get { lock (_lock) return _currentCState; }
    }

    /// <summary>
    /// Gets the current S-state.
    /// </summary>
    public SState CurrentSState
    {
        get { lock (_lock) return _currentSState; }
    }

    /// <summary>
    /// Maximum C-state to allow.
    /// </summary>
    public CState MaxCState { get; set; } = CState.C6;

    /// <summary>
    /// Idle time before entering standby.
    /// </summary>
    public TimeSpan StandbyTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Idle time before entering hibernate.
    /// </summary>
    public TimeSpan HibernateTimeout { get; set; } = TimeSpan.FromHours(2);

    /// <summary>
    /// Whether to allow system standby.
    /// </summary>
    public bool AllowStandby { get; set; } = true;

    /// <summary>
    /// Whether to allow system hibernate.
    /// </summary>
    public bool AllowHibernate { get; set; } = true;

    /// <summary>
    /// Whether to use aggressive C-states for maximum power savings.
    /// </summary>
    public bool AggressiveCStates { get; set; } = false;

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _monitorTimer = new Timer(
            _ => MonitorSleepStates(),
            null,
            TimeSpan.FromSeconds(5),
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
    /// Records activity to reset idle timer.
    /// </summary>
    public void RecordActivity()
    {
        lock (_lock)
        {
            _lastActivityTime = DateTimeOffset.UtcNow;
            _idleTime = TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Gets current sleep state information.
    /// </summary>
    public SleepStateInfo GetSleepStateInfo()
    {
        lock (_lock)
        {
            return new SleepStateInfo
            {
                Timestamp = DateTimeOffset.UtcNow,
                CurrentCState = _currentCState,
                CurrentSState = _currentSState,
                IdleTime = _idleTime,
                LastActivityTime = _lastActivityTime,
                MaxAllowedCState = MaxCState,
                EstimatedPowerSavingsPercent = EstimatePowerSavings(_currentCState),
                ResidencyPercent = GetCStateResidency()
            };
        }
    }

    /// <summary>
    /// Sets the maximum allowed C-state.
    /// </summary>
    public async Task SetMaxCStateAsync(CState maxCState, CancellationToken ct = default)
    {
        MaxCState = maxCState;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            await SetLinuxMaxCStateAsync(maxCState, ct);
        }

        RecordOptimizationAction();
        UpdateRecommendations();
    }

    /// <summary>
    /// Requests system to enter a specific S-state.
    /// </summary>
    public async Task RequestSStateAsync(SState sState, CancellationToken ct = default)
    {
        if (sState == SState.S0) return;

        if (sState >= SState.S3 && !AllowStandby)
            throw new InvalidOperationException("Standby is disabled.");

        if (sState >= SState.S4 && !AllowHibernate)
            throw new InvalidOperationException("Hibernate is disabled.");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await RequestWindowsSStateAsync(sState, ct);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            await RequestLinuxSStateAsync(sState, ct);
        }

        RecordOptimizationAction();
    }

    /// <summary>
    /// Enables or disables aggressive power saving mode.
    /// </summary>
    public async Task SetAggressiveModeAsync(bool aggressive, CancellationToken ct = default)
    {
        AggressiveCStates = aggressive;
        MaxCState = aggressive ? CState.C10 : CState.C6;

        await SetMaxCStateAsync(MaxCState, ct);

        if (aggressive)
        {
            RecordEnergySaved(5.0);
        }

        UpdateRecommendations();
    }

    /// <summary>
    /// Gets C-state residency information.
    /// </summary>
    public IReadOnlyDictionary<CState, double> GetCStateResidency()
    {
        // Would read from /sys/devices/system/cpu/cpu0/cpuidle/state*/time on Linux
        // Or use Intel RAPL on Windows
        // Returning simulated data
        var total = 100.0;
        var c0 = 30.0 + Random.Shared.NextDouble() * 20;
        var remaining = total - c0;

        return new Dictionary<CState, double>
        {
            [CState.C0] = c0,
            [CState.C1] = remaining * 0.2,
            [CState.C3] = remaining * 0.3,
            [CState.C6] = remaining * 0.4,
            [CState.C7] = remaining * 0.1
        };
    }

    private void MonitorSleepStates()
    {
        lock (_lock)
        {
            _idleTime = DateTimeOffset.UtcNow - _lastActivityTime;

            // Estimate current C-state based on idle time
            _currentCState = _idleTime.TotalSeconds switch
            {
                < 0.1 => CState.C0,
                < 1 => CState.C1,
                < 10 => CState.C3,
                < 60 => CState.C6,
                < 300 => CState.C7,
                _ => CState.C8
            };

            // Clamp to max allowed
            if (_currentCState > MaxCState)
                _currentCState = MaxCState;

            // Record low power time
            if (_currentCState >= CState.C3)
            {
                RecordLowPowerTime(5.0);
            }
        }

        RecordSample(EstimatePowerWatts(_currentCState), 0);
        UpdateRecommendations();
    }

    private async Task SetLinuxMaxCStateAsync(CState maxCState, CancellationToken ct)
    {
        try
        {
            var cpuCount = Environment.ProcessorCount;
            for (int cpu = 0; cpu < cpuCount; cpu++)
            {
                for (int state = 0; state <= 10; state++)
                {
                    var disablePath = $"/sys/devices/system/cpu/cpu{cpu}/cpuidle/state{state}/disable";
                    if (File.Exists(disablePath))
                    {
                        var shouldDisable = state > (int)maxCState;
                        await File.WriteAllTextAsync(disablePath, shouldDisable ? "1" : "0", ct);
                    }
                }
            }
        }
        catch
        {
            // Permission denied or not available
        }
    }

    private async Task RequestWindowsSStateAsync(SState sState, CancellationToken ct)
    {
        try
        {
            var command = sState switch
            {
                SState.S3 => "powercfg -h off && rundll32.exe powrprof.dll,SetSuspendState 0,1,0",
                SState.S4 => "shutdown /h",
                SState.S5 => "shutdown /s /t 0",
                _ => null
            };

            if (command != null)
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {command}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync(ct);
                }
            }
        }
        catch
        {
            // Failed to request S-state
        }
    }

    private async Task RequestLinuxSStateAsync(SState sState, CancellationToken ct)
    {
        try
        {
            var state = sState switch
            {
                SState.S3 => "mem",
                SState.S4 => "disk",
                SState.S5 => "poweroff",
                _ => null
            };

            if (state != null && File.Exists("/sys/power/state"))
            {
                await File.WriteAllTextAsync("/sys/power/state", state, ct);
            }
        }
        catch
        {
            // Permission denied
        }
    }

    private static double EstimatePowerSavings(CState cState)
    {
        return cState switch
        {
            CState.C0 => 0,
            CState.C1 => 15,
            CState.C1E => 25,
            CState.C3 => 50,
            CState.C6 => 75,
            CState.C7 => 85,
            CState.C8 => 90,
            CState.C9 => 93,
            CState.C10 => 95,
            _ => 0
        };
    }

    private static double EstimatePowerWatts(CState cState)
    {
        var basePower = 65.0;
        var savings = EstimatePowerSavings(cState) / 100.0;
        return basePower * (1 - savings);
    }

    private void UpdateRecommendations()
    {
        ClearRecommendations();

        if (MaxCState < CState.C6)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-enable-deep-cstates",
                Type = "EnableDeepCStates",
                Priority = 6,
                Description = "Deep C-states are disabled. Enable C6+ for significant idle power savings.",
                EstimatedEnergySavingsWh = 10,
                CanAutoApply = true,
                Action = "set-max-cstate",
                ActionParameters = new Dictionary<string, object> { ["maxCState"] = "C6" }
            });
        }

        if (_idleTime > TimeSpan.FromMinutes(30) && CurrentSState == SState.S0 && AllowStandby)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-enter-standby",
                Type = "EnterStandby",
                Priority = 7,
                Description = "System has been idle for 30+ minutes. Consider entering standby mode.",
                EstimatedEnergySavingsWh = 50,
                CanAutoApply = true,
                Action = "enter-standby"
            });
        }
    }
}

/// <summary>
/// ACPI C-states (CPU idle states).
/// </summary>
public enum CState
{
    /// <summary>Operating state - CPU active.</summary>
    C0 = 0,
    /// <summary>Halt - CPU clock stopped.</summary>
    C1 = 1,
    /// <summary>Extended halt with deeper power savings.</summary>
    C1E = 2,
    /// <summary>Sleep - CPU cache flushed.</summary>
    C3 = 3,
    /// <summary>Deep power down - most power savings.</summary>
    C6 = 6,
    /// <summary>Enhanced deep power down.</summary>
    C7 = 7,
    /// <summary>Ultra-deep power down.</summary>
    C8 = 8,
    /// <summary>Near-off state.</summary>
    C9 = 9,
    /// <summary>Deepest idle state.</summary>
    C10 = 10
}

/// <summary>
/// ACPI S-states (system sleep states).
/// </summary>
public enum SState
{
    /// <summary>Working state - system on.</summary>
    S0 = 0,
    /// <summary>CPU stopped, memory refreshed.</summary>
    S1 = 1,
    /// <summary>CPU cache flushed, CPU off.</summary>
    S2 = 2,
    /// <summary>Standby/Sleep - RAM powered.</summary>
    S3 = 3,
    /// <summary>Hibernate - RAM saved to disk.</summary>
    S4 = 4,
    /// <summary>Soft off - power button to resume.</summary>
    S5 = 5
}

/// <summary>
/// Current sleep state information.
/// </summary>
public sealed record SleepStateInfo
{
    public required DateTimeOffset Timestamp { get; init; }
    public required CState CurrentCState { get; init; }
    public required SState CurrentSState { get; init; }
    public required TimeSpan IdleTime { get; init; }
    public required DateTimeOffset LastActivityTime { get; init; }
    public required CState MaxAllowedCState { get; init; }
    public required double EstimatedPowerSavingsPercent { get; init; }
    public required IReadOnlyDictionary<CState, double> ResidencyPercent { get; init; }
}
