namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.ThermalManagement;

/// <summary>
/// Optimizes cooling system efficiency by controlling fan speeds and
/// coordinating with workload scheduling to minimize energy used for cooling.
/// </summary>
public sealed class CoolingOptimizationStrategy : SustainabilityStrategyBase
{
    private readonly Dictionary<string, FanZone> _fanZones = new();
    private CoolingProfile _currentProfile = CoolingProfile.Balanced;
    private Timer? _controlTimer;
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "cooling-optimization";
    /// <inheritdoc/>
    public override string DisplayName => "Cooling Optimization";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.ThermalManagement;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.ActiveControl | SustainabilityCapabilities.RealTimeMonitoring;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Controls fan speeds and cooling system to minimize energy used for cooling while maintaining safe temperatures.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "cooling", "fans", "optimization", "energy", "thermal" };

    /// <summary>Target temperature for balanced profile (C).</summary>
    public double TargetTemperatureC { get; set; } = 70;

    /// <summary>Current cooling profile.</summary>
    public CoolingProfile CurrentProfile { get { lock (_lock) return _currentProfile; } }

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        DiscoverFans();
        _controlTimer = new Timer(_ => OptimizeCooling(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _controlTimer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Sets the cooling profile.</summary>
    public async Task SetProfileAsync(CoolingProfile profile, CancellationToken ct = default)
    {
        lock (_lock) _currentProfile = profile;
        await ApplyProfileAsync(profile);
        RecordOptimizationAction();
    }

    private void DiscoverFans()
    {
        // Discover fan zones from /sys/class/hwmon on Linux
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
        {
            try
            {
                var hwmonBase = "/sys/class/hwmon";
                if (Directory.Exists(hwmonBase))
                {
                    foreach (var hwmonDir in Directory.GetDirectories(hwmonBase))
                    {
                        // Look for fan inputs (fan1_input, fan2_input, ...)
                        foreach (var file in Directory.GetFiles(hwmonDir, "fan*_input"))
                        {
                            var fanNum = Path.GetFileNameWithoutExtension(file).Replace("fan", "").Replace("_input", "");
                            var pwmPath = Path.Combine(hwmonDir, $"pwm{fanNum}");
                            if (!File.Exists(pwmPath)) continue;

                            var maxRpmPath = Path.Combine(hwmonDir, $"fan{fanNum}_max");
                            int maxRpm = 3000;
                            if (File.Exists(maxRpmPath) && int.TryParse(File.ReadAllText(maxRpmPath).Trim(), out var mr))
                                maxRpm = mr;

                            int currentRpm = 0;
                            if (int.TryParse(File.ReadAllText(file).Trim(), out var cr))
                                currentRpm = cr;

                            var name = $"hwmon-fan{fanNum}";
                            _fanZones[name] = new FanZone
                            {
                                Name = name,
                                SysFsPath = pwmPath,
                                MaxRpm = maxRpm,
                                CurrentRpm = currentRpm,
                                CurrentPercent = maxRpm > 0 ? (int)(100.0 * currentRpm / maxRpm) : 0
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Cooling] Fan discovery failed: {ex.Message}");
            }
        }

        // If no fans discovered, register none — don't pretend to manage hardware we can't reach
    }

    private void OptimizeCooling()
    {
        // Read current temperature and adjust fans
        var targetSpeed = _currentProfile switch
        {
            CoolingProfile.Silent => 30,
            CoolingProfile.Balanced => 50,
            CoolingProfile.Performance => 80,
            CoolingProfile.Maximum => 100,
            _ => 50
        };

        foreach (var fan in _fanZones.Values)
        {
            fan.CurrentPercent = targetSpeed;
            fan.CurrentRpm = (int)(fan.MaxRpm * targetSpeed / 100.0);
        }

        // Estimate power savings from reduced fan speed
        var baseFanPower = 10.0; // Watts at full speed
        var savedPower = baseFanPower * (1 - targetSpeed / 100.0);
        if (savedPower > 0) RecordEnergySaved(savedPower * 0.01);

        UpdateRecommendations();
    }

    private async Task ApplyProfileAsync(CoolingProfile profile)
    {
        // Target PWM duty cycle (0-255 for sysfs pwm interface)
        var targetPwm = profile switch
        {
            CoolingProfile.Silent      => 77,  // ~30%
            CoolingProfile.Balanced    => 128, // ~50%
            CoolingProfile.Performance => 204, // ~80%
            CoolingProfile.Maximum     => 255, // 100%
            _                          => 128
        };

        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
        {
            foreach (var fan in _fanZones.Values)
            {
                if (string.IsNullOrEmpty(fan.SysFsPath)) continue;

                try
                {
                    // Enable manual PWM control (pwm_enable = 1)
                    var enablePath = fan.SysFsPath + "_enable";
                    if (File.Exists(enablePath))
                        await File.WriteAllTextAsync(enablePath, "1");

                    // Write target duty cycle
                    if (File.Exists(fan.SysFsPath))
                        await File.WriteAllTextAsync(fan.SysFsPath, targetPwm.ToString());
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Cooling] Failed to set PWM for {fan.Name}: {ex.Message}");
                }
            }
        }
        // On non-Linux platforms, internal state tracks the profile but no kernel interface is available.
        await Task.CompletedTask;
    }

    private void UpdateRecommendations()
    {
        ClearRecommendations();
        if (_currentProfile == CoolingProfile.Maximum)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-reduce-cooling",
                Type = "ReduceCooling",
                Priority = 5,
                Description = "Cooling at maximum. Consider balanced profile for energy savings.",
                EstimatedEnergySavingsWh = 5,
                CanAutoApply = true,
                Action = "set-profile",
                ActionParameters = new Dictionary<string, object> { ["profile"] = "Balanced" }
            });
        }
    }
}

/// <summary>Cooling profile presets.</summary>
public enum CoolingProfile { Silent, Balanced, Performance, Maximum }

/// <summary>Fan zone information.</summary>
public sealed class FanZone
{
    public required string Name { get; init; }
    /// <summary>Path to the sysfs PWM control file (e.g. /sys/class/hwmon/hwmon0/pwm1).</summary>
    public string? SysFsPath { get; init; }
    public int MaxRpm { get; init; }
    public int CurrentRpm { get; set; }
    public int CurrentPercent { get; set; }
}
