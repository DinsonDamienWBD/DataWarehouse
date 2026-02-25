namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.ThermalManagement;

/// <summary>
/// Optimizes liquid cooling systems for data centers including
/// coolant flow rates, pump speeds, and heat exchanger efficiency.
/// </summary>
public sealed class LiquidCoolingOptimizationStrategy : SustainabilityStrategyBase
{
    private readonly Dictionary<string, CoolingLoop> _loops = new();
    private Timer? _monitorTimer;
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "liquid-cooling-optimization";
    /// <inheritdoc/>
    public override string DisplayName => "Liquid Cooling Optimization";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.ThermalManagement;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.RealTimeMonitoring | SustainabilityCapabilities.ActiveControl;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Optimizes liquid cooling systems including flow rates, pump speeds, and heat exchanger efficiency.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "cooling", "liquid", "datacenter", "pump", "heat-exchanger", "pue" };

    /// <summary>Target delta-T for cooling loops (Celsius).</summary>
    public double TargetDeltaT { get; set; } = 10;
    /// <summary>Maximum inlet temperature (Celsius).</summary>
    public double MaxInletTemperature { get; set; } = 40;

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _monitorTimer = new Timer(_ => MonitorLoops(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _monitorTimer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Registers a cooling loop.</summary>
    public void RegisterLoop(string loopId, string name, double maxFlowRateLpm, double pumpPowerWatts)
    {
        lock (_lock)
        {
            _loops[loopId] = new CoolingLoop
            {
                LoopId = loopId,
                Name = name,
                MaxFlowRateLpm = maxFlowRateLpm,
                PumpPowerWatts = pumpPowerWatts,
                CurrentFlowRateLpm = maxFlowRateLpm * 0.5
            };
        }
    }

    /// <summary>Updates loop sensor readings.</summary>
    public void UpdateLoopReadings(string loopId, double inletTempC, double outletTempC, double flowRateLpm)
    {
        lock (_lock)
        {
            if (_loops.TryGetValue(loopId, out var loop))
            {
                loop.InletTemperatureC = inletTempC;
                loop.OutletTemperatureC = outletTempC;
                loop.CurrentFlowRateLpm = flowRateLpm;
                loop.DeltaT = outletTempC - inletTempC;

                // Calculate heat removal (Q = m * Cp * dT)
                // Water: Cp = 4.186 kJ/kg-K, density = 1 kg/L
                loop.HeatRemovalKw = flowRateLpm * 4.186 * loop.DeltaT / 60;
            }
        }
        RecordSample(flowRateLpm, 0);
    }

    /// <summary>Gets optimal flow rate for a loop.</summary>
    public FlowRateRecommendation GetOptimalFlowRate(string loopId, double heatLoadKw)
    {
        lock (_lock)
        {
            if (!_loops.TryGetValue(loopId, out var loop))
                return new FlowRateRecommendation { LoopId = loopId, Success = false, Reason = "Loop not found" };

            // Q = m * Cp * dT => m = Q / (Cp * dT)
            // Flow rate (L/min) = Q (kW) * 60 / (4.186 * dT)
            var optimalFlowRate = (heatLoadKw * 60) / (4.186 * TargetDeltaT);
            optimalFlowRate = Math.Min(optimalFlowRate, loop.MaxFlowRateLpm);
            optimalFlowRate = Math.Max(optimalFlowRate, loop.MaxFlowRateLpm * 0.1); // Min 10% flow

            var pumpPowerAtOptimal = loop.PumpPowerWatts * Math.Pow(optimalFlowRate / loop.MaxFlowRateLpm, 3);
            var currentPumpPower = loop.PumpPowerWatts * Math.Pow(loop.CurrentFlowRateLpm / loop.MaxFlowRateLpm, 3);
            var savings = currentPumpPower - pumpPowerAtOptimal;

            return new FlowRateRecommendation
            {
                LoopId = loopId,
                Success = true,
                CurrentFlowRateLpm = loop.CurrentFlowRateLpm,
                OptimalFlowRateLpm = optimalFlowRate,
                CurrentPumpPowerWatts = currentPumpPower,
                OptimalPumpPowerWatts = pumpPowerAtOptimal,
                PotentialSavingsWatts = savings,
                Reason = savings > 0 ? "Reduce flow rate to save energy" : "Current flow rate is optimal"
            };
        }
    }

    /// <summary>Sets pump speed for a loop.</summary>
    public bool SetFlowRate(string loopId, double flowRateLpm)
    {
        lock (_lock)
        {
            if (!_loops.TryGetValue(loopId, out var loop)) return false;
            loop.CurrentFlowRateLpm = Math.Clamp(flowRateLpm, 0, loop.MaxFlowRateLpm);
            RecordOptimizationAction();
            return true;
        }
    }

    private void MonitorLoops()
    {
        ClearRecommendations();
        lock (_lock)
        {
            foreach (var loop in _loops.Values)
            {
                if (loop.InletTemperatureC > MaxInletTemperature)
                {
                    AddRecommendation(new SustainabilityRecommendation
                    {
                        RecommendationId = $"{StrategyId}-{loop.LoopId}-hot",
                        Type = "HighInletTemp",
                        Priority = 8,
                        Description = $"Loop {loop.Name} inlet at {loop.InletTemperatureC:F1}C exceeds {MaxInletTemperature}C",
                        CanAutoApply = true,
                        Action = "increase-flow-rate"
                    });
                }
                else if (loop.DeltaT < TargetDeltaT * 0.5 && loop.CurrentFlowRateLpm > loop.MaxFlowRateLpm * 0.3)
                {
                    var recommendation = GetOptimalFlowRate(loop.LoopId, loop.HeatRemovalKw);
                    if (recommendation.PotentialSavingsWatts > 10)
                    {
                        AddRecommendation(new SustainabilityRecommendation
                        {
                            RecommendationId = $"{StrategyId}-{loop.LoopId}-reduce",
                            Type = "ReduceFlowRate",
                            Priority = 5,
                            Description = $"Reduce {loop.Name} flow from {loop.CurrentFlowRateLpm:F0} to {recommendation.OptimalFlowRateLpm:F0} L/min",
                            EstimatedEnergySavingsWh = recommendation.PotentialSavingsWatts * 24,
                            CanAutoApply = true,
                            Action = "reduce-flow-rate"
                        });
                    }
                }
            }
        }
    }
}

/// <summary>Cooling loop information.</summary>
public sealed class CoolingLoop
{
    public required string LoopId { get; init; }
    public required string Name { get; init; }
    public required double MaxFlowRateLpm { get; init; }
    public required double PumpPowerWatts { get; init; }
    public double CurrentFlowRateLpm { get; set; }
    public double InletTemperatureC { get; set; }
    public double OutletTemperatureC { get; set; }
    public double DeltaT { get; set; }
    public double HeatRemovalKw { get; set; }
}

/// <summary>Flow rate recommendation.</summary>
public sealed record FlowRateRecommendation
{
    public required string LoopId { get; init; }
    public bool Success { get; init; }
    public string? Reason { get; init; }
    public double CurrentFlowRateLpm { get; init; }
    public double OptimalFlowRateLpm { get; init; }
    public double CurrentPumpPowerWatts { get; init; }
    public double OptimalPumpPowerWatts { get; init; }
    public double PotentialSavingsWatts { get; init; }
}
