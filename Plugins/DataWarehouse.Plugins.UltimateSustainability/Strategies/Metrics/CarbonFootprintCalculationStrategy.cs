namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.Metrics;

/// <summary>
/// Calculates carbon footprint from energy consumption using regional
/// emission factors. Supports Scope 1, 2, and 3 emissions tracking.
/// </summary>
public sealed class CarbonFootprintCalculationStrategy : SustainabilityStrategyBase
{
    private double _totalScope2Grams;
    private double _totalScope3Grams;
    private Func<Task<double>>? _carbonIntensityProvider;
    private Func<Task<double>>? _energyProvider;
    private Timer? _calcTimer;
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "carbon-footprint-calculation";
    /// <inheritdoc/>
    public override string DisplayName => "Carbon Footprint Calculation";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.Metrics;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.CarbonCalculation | SustainabilityCapabilities.Reporting;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Calculates carbon footprint from energy using regional emission factors. Tracks Scope 2 and 3 emissions.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "carbon", "footprint", "emissions", "scope2", "scope3", "ghg" };

    /// <summary>Default carbon intensity (gCO2e/kWh).</summary>
    public double DefaultCarbonIntensity { get; set; } = 400;

    /// <summary>Scope 3 multiplier for embodied emissions.</summary>
    public double Scope3Multiplier { get; set; } = 0.1;

    /// <summary>Total Scope 2 emissions (grams CO2e).</summary>
    public double TotalScope2Grams { get { lock (_lock) return _totalScope2Grams; } }

    /// <summary>Total Scope 3 emissions (grams CO2e).</summary>
    public double TotalScope3Grams { get { lock (_lock) return _totalScope3Grams; } }

    /// <summary>Total emissions (grams CO2e).</summary>
    public double TotalEmissionsGrams => TotalScope2Grams + TotalScope3Grams;

    /// <summary>Configures carbon intensity source.</summary>
    public void SetCarbonIntensityProvider(Func<Task<double>> provider) => _carbonIntensityProvider = provider;

    /// <summary>Configures energy source.</summary>
    public void SetEnergyProvider(Func<Task<double>> provider) => _energyProvider = provider;

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _calcTimer = new Timer(async _ => await CalculateEmissionsAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _calcTimer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Calculates emissions for a given energy amount.</summary>
    public async Task<EmissionResult> CalculateEmissionsAsync(double energyWh)
    {
        var intensity = _carbonIntensityProvider != null
            ? await _carbonIntensityProvider()
            : DefaultCarbonIntensity;

        var scope2 = (energyWh / 1000.0) * intensity;
        var scope3 = scope2 * Scope3Multiplier;

        return new EmissionResult
        {
            EnergyWh = energyWh,
            CarbonIntensity = intensity,
            Scope2Grams = scope2,
            Scope3Grams = scope3,
            TotalGrams = scope2 + scope3
        };
    }

    /// <summary>Records emissions.</summary>
    public void RecordEmissions(double scope2Grams, double scope3Grams = 0)
    {
        lock (_lock)
        {
            _totalScope2Grams += scope2Grams;
            _totalScope3Grams += scope3Grams;
        }
    }

    /// <summary>Gets emissions summary.</summary>
    public EmissionsSummary GetSummary()
    {
        lock (_lock)
        {
            return new EmissionsSummary
            {
                TotalScope2Grams = _totalScope2Grams,
                TotalScope3Grams = _totalScope3Grams,
                TotalEmissionsGrams = _totalScope2Grams + _totalScope3Grams,
                TotalKgCO2e = (_totalScope2Grams + _totalScope3Grams) / 1000.0,
                TotalTonsCO2e = (_totalScope2Grams + _totalScope3Grams) / 1_000_000.0
            };
        }
    }

    private async Task CalculateEmissionsAsync()
    {
        if (_energyProvider == null) return;

        var energyWh = await _energyProvider();
        var result = await CalculateEmissionsAsync(energyWh);

        RecordEmissions(result.Scope2Grams, result.Scope3Grams);
        RecordSample(0, result.TotalGrams);
    }
}

/// <summary>Emission calculation result.</summary>
public sealed record EmissionResult
{
    public required double EnergyWh { get; init; }
    public required double CarbonIntensity { get; init; }
    public required double Scope2Grams { get; init; }
    public required double Scope3Grams { get; init; }
    public required double TotalGrams { get; init; }
}

/// <summary>Emissions summary.</summary>
public sealed record EmissionsSummary
{
    public required double TotalScope2Grams { get; init; }
    public required double TotalScope3Grams { get; init; }
    public required double TotalEmissionsGrams { get; init; }
    public required double TotalKgCO2e { get; init; }
    public required double TotalTonsCO2e { get; init; }
}
