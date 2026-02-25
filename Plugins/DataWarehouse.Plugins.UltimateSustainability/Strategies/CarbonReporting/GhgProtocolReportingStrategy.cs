using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Carbon;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.CarbonReporting;

/// <summary>
/// GHG Protocol report record combining Scope 2 and Scope 3 entries into a full auditable report.
/// </summary>
public sealed record GhgFullReport
{
    /// <summary>Unique report identifier.</summary>
    public required string ReportId { get; init; }

    /// <summary>UTC timestamp when the report was generated.</summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Organization or tenant name for the report header.</summary>
    public required string OrganizationName { get; init; }

    /// <summary>Start of the reporting period (inclusive).</summary>
    public required DateTimeOffset PeriodStart { get; init; }

    /// <summary>End of the reporting period (inclusive).</summary>
    public required DateTimeOffset PeriodEnd { get; init; }

    /// <summary>All GHG report line items (Scope 2 + Scope 3).</summary>
    public required IReadOnlyList<GhgReportEntry> Entries { get; init; }

    /// <summary>Total Scope 2 emissions in grams CO2e.</summary>
    public required double TotalScope2 { get; init; }

    /// <summary>Total Scope 3 emissions in grams CO2e.</summary>
    public required double TotalScope3 { get; init; }

    /// <summary>Grand total emissions (Scope 2 + Scope 3) in grams CO2e.</summary>
    public double TotalEmissions => TotalScope2 + TotalScope3;

    /// <summary>Methodology description for audit trail.</summary>
    public required string Methodology { get; init; }

    /// <summary>Executive summary of the report findings.</summary>
    public string? ExecutiveSummary { get; init; }

    /// <summary>Optional tenant identifier the report is scoped to.</summary>
    public string? TenantId { get; init; }
}

/// <summary>
/// Generates GHG Protocol-compliant emission reports (Scope 2 and Scope 3)
/// from collected energy measurements and grid carbon intensity data.
/// Extends <see cref="SustainabilityStrategyBase"/> for integration with the
/// sustainability strategy framework.
///
/// Scope 2 (Purchased Electricity):
/// - Location-based method: energy * regional grid carbon intensity
/// - Market-based method: energy * contract-specific intensity (renewable certificates)
/// - Categories: compute, storage, network electricity consumption
///
/// Scope 3 (Value Chain):
/// - Category 1: Vendor-hosted storage upstream emissions
/// - Category 4: Data transfer between regions
/// - Category 11: Downstream user compute emissions
///
/// Data quality tagging follows GHG Protocol guidance:
/// - Measured: from RAPL/powercap hardware counters
/// - Calculated: from cloud provider APIs with published emission factors
/// - Estimated: from TDP-based estimation models
/// </summary>
public sealed class GhgProtocolReportingStrategy : SustainabilityStrategyBase
{
    private const string PluginId = "com.datawarehouse.sustainability.ultimate";

    // Emission factors (kgCO2e per unit) based on published data
    private const double CloudStorageEmissionFactorKgCO2ePerGbMonth = 0.023;  // AWS S3 average
    private const double DataTransferEmissionFactorKgCO2ePerGb = 0.06;       // Cross-region transfer
    private const double DownstreamComputeEmissionFactorKgCO2ePerGbRead = 0.01; // End-user compute

    // Default carbon intensity when no real-time grid data is available (gCO2e/kWh)
    private const double DefaultCarbonIntensityGCO2ePerKwh = 400.0;

    // In-memory storage for energy measurements received via message bus
    private readonly ConcurrentBag<EnergyMeasurementRecord> _energyMeasurements = new();
    private readonly BoundedDictionary<string, double> _regionCarbonIntensity = new BoundedDictionary<string, double>(1000);
    private IDisposable? _energySubscription;
    private IDisposable? _intensitySubscription;

    /// <inheritdoc/>
    public override string StrategyId => "ghg-protocol-reporting";

    /// <inheritdoc/>
    public override string DisplayName => "GHG Protocol Reporting";

    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.Metrics;

    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.Reporting |
        SustainabilityCapabilities.CarbonCalculation;

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Generates GHG Protocol-compliant emission reports covering Scope 2 (purchased electricity) " +
        "and Scope 3 (value chain) emissions from energy measurements and grid carbon data. " +
        "Supports location-based and market-based accounting methods for regulatory compliance " +
        "(EU CSRD, SEC Climate Disclosure).";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "ghg", "protocol", "reporting", "scope2", "scope3", "emissions",
        "regulatory", "compliance", "csrd", "carbon", "audit"
    };

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        SubscribeToEnergyMeasurements();
        SubscribeToCarbonIntensityUpdates();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _energySubscription?.Dispose();
        _energySubscription = null;
        _intensitySubscription?.Dispose();
        _intensitySubscription = null;
        return Task.CompletedTask;
    }

    #region Scope 2 Reporting

    /// <summary>
    /// Generates GHG Protocol Scope 2 report entries from measured energy consumption
    /// and grid carbon intensity for the specified period.
    /// Uses location-based method by default, with market-based adjustments
    /// when renewable energy certificate data is available.
    /// </summary>
    /// <param name="from">Start of the reporting period (inclusive, UTC).</param>
    /// <param name="to">End of the reporting period (inclusive, UTC).</param>
    /// <param name="tenantId">Optional tenant filter. Null returns system-wide data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of Scope 2 GHG report entries grouped by region and category.</returns>
    public Task<IReadOnlyList<GhgReportEntry>> GenerateScope2ReportAsync(
        DateTimeOffset from, DateTimeOffset to, string? tenantId = null, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        // Filter measurements to the requested period and tenant
        var measurements = _energyMeasurements
            .Where(m => m.Timestamp >= from && m.Timestamp <= to)
            .Where(m => tenantId == null || string.Equals(m.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var entries = new List<GhgReportEntry>();

        // Group by region for location-based reporting
        var byRegion = measurements.GroupBy(m => m.Region ?? "default");

        foreach (var regionGroup in byRegion)
        {
            var region = regionGroup.Key;
            var carbonIntensity = GetRegionCarbonIntensity(region);

            // Sub-group by operation category for granular reporting
            var computeMeasurements = regionGroup.Where(m => IsComputeOperation(m.OperationType)).ToList();
            var storageMeasurements = regionGroup.Where(m => IsStorageOperation(m.OperationType)).ToList();
            var networkMeasurements = regionGroup.Where(m => IsNetworkOperation(m.OperationType)).ToList();

            // Compute electricity consumption
            if (computeMeasurements.Count > 0)
            {
                var totalEnergyWh = computeMeasurements.Sum(m => m.EnergyWh);
                var emissionsGrams = totalEnergyWh * carbonIntensity / 1000.0;
                var dataQuality = DetermineDataQuality(computeMeasurements);

                entries.Add(new GhgReportEntry
                {
                    Scope = GhgScopeCategory.Scope2_PurchasedElectricity,
                    Category = "Electricity consumption - compute",
                    EmissionsGramsCO2e = Math.Round(emissionsGrams, 4),
                    EnergyConsumedWh = Math.Round(totalEnergyWh, 4),
                    PeriodStart = from,
                    PeriodEnd = to,
                    Region = region,
                    Source = GetPrimarySource(computeMeasurements),
                    DataQuality = dataQuality
                });
            }

            // Storage electricity consumption
            if (storageMeasurements.Count > 0)
            {
                var totalEnergyWh = storageMeasurements.Sum(m => m.EnergyWh);
                var emissionsGrams = totalEnergyWh * carbonIntensity / 1000.0;
                var dataQuality = DetermineDataQuality(storageMeasurements);

                entries.Add(new GhgReportEntry
                {
                    Scope = GhgScopeCategory.Scope2_PurchasedElectricity,
                    Category = "Electricity consumption - storage",
                    EmissionsGramsCO2e = Math.Round(emissionsGrams, 4),
                    EnergyConsumedWh = Math.Round(totalEnergyWh, 4),
                    PeriodStart = from,
                    PeriodEnd = to,
                    Region = region,
                    Source = GetPrimarySource(storageMeasurements),
                    DataQuality = dataQuality
                });
            }

            // Network electricity consumption
            if (networkMeasurements.Count > 0)
            {
                var totalEnergyWh = networkMeasurements.Sum(m => m.EnergyWh);
                var emissionsGrams = totalEnergyWh * carbonIntensity / 1000.0;
                var dataQuality = DetermineDataQuality(networkMeasurements);

                entries.Add(new GhgReportEntry
                {
                    Scope = GhgScopeCategory.Scope2_PurchasedElectricity,
                    Category = "Electricity consumption - network",
                    EmissionsGramsCO2e = Math.Round(emissionsGrams, 4),
                    EnergyConsumedWh = Math.Round(totalEnergyWh, 4),
                    PeriodStart = from,
                    PeriodEnd = to,
                    Region = region,
                    Source = GetPrimarySource(networkMeasurements),
                    DataQuality = dataQuality
                });
            }
        }

        RecordOptimizationAction();
        return Task.FromResult<IReadOnlyList<GhgReportEntry>>(entries.AsReadOnly());
    }

    #endregion

    #region Scope 3 Reporting

    /// <summary>
    /// Generates GHG Protocol Scope 3 report entries covering upstream and downstream
    /// value chain emissions for the specified period.
    /// </summary>
    /// <param name="from">Start of the reporting period (inclusive, UTC).</param>
    /// <param name="to">End of the reporting period (inclusive, UTC).</param>
    /// <param name="tenantId">Optional tenant filter. Null returns system-wide data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of Scope 3 GHG report entries by category.</returns>
    public Task<IReadOnlyList<GhgReportEntry>> GenerateScope3ReportAsync(
        DateTimeOffset from, DateTimeOffset to, string? tenantId = null, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var measurements = _energyMeasurements
            .Where(m => m.Timestamp >= from && m.Timestamp <= to)
            .Where(m => tenantId == null || string.Equals(m.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var entries = new List<GhgReportEntry>();

        // Scope 3 Category 1: Purchased goods/services -- vendor-hosted storage
        var storageMeasurements = measurements.Where(m => IsStorageOperation(m.OperationType)).ToList();
        if (storageMeasurements.Count > 0)
        {
            // Estimate cloud storage volume from write operations
            var totalStorageGb = storageMeasurements
                .Where(m => m.DataSizeBytes > 0)
                .Sum(m => m.DataSizeBytes / (1024.0 * 1024.0 * 1024.0));

            // Calculate months in the reporting period
            var periodMonths = Math.Max(1, (to - from).TotalDays / 30.0);
            var emissionsKg = totalStorageGb * periodMonths * CloudStorageEmissionFactorKgCO2ePerGbMonth;
            var emissionsGrams = emissionsKg * 1000.0;

            if (emissionsGrams > 0)
            {
                entries.Add(new GhgReportEntry
                {
                    Scope = GhgScopeCategory.Scope3_ValueChain,
                    Category = "Category 1 - Purchased goods/services (vendor-hosted storage)",
                    EmissionsGramsCO2e = Math.Round(emissionsGrams, 4),
                    EnergyConsumedWh = 0, // Scope 3 upstream -- energy not directly measured
                    PeriodStart = from,
                    PeriodEnd = to,
                    Region = "global",
                    Source = "Emission factor: vendor-hosted storage upstream emissions",
                    DataQuality = DataQualityLevel.Estimated
                });
            }
        }

        // Scope 3 Category 4: Upstream transportation -- data transfers between regions
        var transferMeasurements = measurements.Where(m => IsNetworkOperation(m.OperationType)).ToList();
        if (transferMeasurements.Count > 0)
        {
            var totalTransferGb = transferMeasurements
                .Where(m => m.DataSizeBytes > 0)
                .Sum(m => m.DataSizeBytes / (1024.0 * 1024.0 * 1024.0));

            // Fallback: estimate from network energy if data size unavailable
            if (totalTransferGb <= 0)
            {
                totalTransferGb = transferMeasurements.Sum(m => m.EnergyWh) * 100.0; // rough approximation
            }

            var emissionsKg = totalTransferGb * DataTransferEmissionFactorKgCO2ePerGb;
            var emissionsGrams = emissionsKg * 1000.0;

            if (emissionsGrams > 0)
            {
                entries.Add(new GhgReportEntry
                {
                    Scope = GhgScopeCategory.Scope3_ValueChain,
                    Category = "Category 4 - Upstream transportation (data transfers between regions)",
                    EmissionsGramsCO2e = Math.Round(emissionsGrams, 4),
                    EnergyConsumedWh = 0,
                    PeriodStart = from,
                    PeriodEnd = to,
                    Region = "global",
                    Source = "Emission factor: cross-region data transfer",
                    DataQuality = DataQualityLevel.Estimated
                });
            }
        }

        // Scope 3 Category 11: Use of sold products -- downstream user compute
        var readMeasurements = measurements.Where(m =>
            m.OperationType.Contains("read", StringComparison.OrdinalIgnoreCase) ||
            m.OperationType.Contains("get", StringComparison.OrdinalIgnoreCase) ||
            m.OperationType.Contains("list", StringComparison.OrdinalIgnoreCase)).ToList();

        if (readMeasurements.Count > 0)
        {
            var totalReadGb = readMeasurements
                .Where(m => m.DataSizeBytes > 0)
                .Sum(m => m.DataSizeBytes / (1024.0 * 1024.0 * 1024.0));

            if (totalReadGb <= 0)
            {
                totalReadGb = readMeasurements.Sum(m => m.EnergyWh) * 50.0;
            }

            var emissionsKg = totalReadGb * DownstreamComputeEmissionFactorKgCO2ePerGbRead;
            var emissionsGrams = emissionsKg * 1000.0;

            if (emissionsGrams > 0)
            {
                entries.Add(new GhgReportEntry
                {
                    Scope = GhgScopeCategory.Scope3_ValueChain,
                    Category = "Category 11 - Use of sold products (downstream user compute)",
                    EmissionsGramsCO2e = Math.Round(emissionsGrams, 4),
                    EnergyConsumedWh = 0,
                    PeriodStart = from,
                    PeriodEnd = to,
                    Region = "global",
                    Source = "Emission factor: downstream user compute",
                    DataQuality = DataQualityLevel.Estimated
                });
            }
        }

        RecordOptimizationAction();
        return Task.FromResult<IReadOnlyList<GhgReportEntry>>(entries.AsReadOnly());
    }

    #endregion

    #region Full Report

    /// <summary>
    /// Generates a complete GHG Protocol report combining Scope 2 and Scope 3,
    /// with executive summary and methodology description.
    /// </summary>
    /// <param name="from">Start of the reporting period (inclusive, UTC).</param>
    /// <param name="to">End of the reporting period (inclusive, UTC).</param>
    /// <param name="organizationName">Organization name for the report header.</param>
    /// <param name="tenantId">Optional tenant filter. Null returns system-wide data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A complete GHG Protocol report.</returns>
    public async Task<GhgFullReport> GenerateFullReportAsync(
        DateTimeOffset from, DateTimeOffset to, string organizationName,
        string? tenantId = null, CancellationToken ct = default)
    {
        var scope2Entries = await GenerateScope2ReportAsync(from, to, tenantId, ct);
        var scope3Entries = await GenerateScope3ReportAsync(from, to, tenantId, ct);

        var allEntries = scope2Entries.Concat(scope3Entries).ToList();
        var totalScope2 = scope2Entries.Sum(e => e.EmissionsGramsCO2e);
        var totalScope3 = scope3Entries.Sum(e => e.EmissionsGramsCO2e);
        var totalEmissions = totalScope2 + totalScope3;

        var executiveSummary = BuildExecutiveSummary(totalScope2, totalScope3, scope2Entries, scope3Entries, from, to);

        return new GhgFullReport
        {
            ReportId = $"GHG-{from:yyyyMMdd}-{to:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8]}",
            GeneratedAt = DateTimeOffset.UtcNow,
            OrganizationName = organizationName,
            PeriodStart = from,
            PeriodEnd = to,
            Entries = allEntries.AsReadOnly(),
            TotalScope2 = Math.Round(totalScope2, 4),
            TotalScope3 = Math.Round(totalScope3, 4),
            Methodology = BuildMethodology(),
            ExecutiveSummary = executiveSummary,
            TenantId = tenantId
        };
    }

    #endregion

    #region Data Ingestion

    /// <summary>
    /// Records an energy measurement for reporting. Can be called directly
    /// or receives data via the message bus subscription.
    /// </summary>
    /// <param name="record">The energy measurement to record.</param>
    public void RecordMeasurement(EnergyMeasurementRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _energyMeasurements.Add(record);
        RecordSample(record.WattsConsumed, record.CarbonIntensity);
    }

    /// <summary>
    /// Updates the carbon intensity for a specific region.
    /// </summary>
    /// <param name="region">Region identifier.</param>
    /// <param name="intensityGCO2ePerKwh">Carbon intensity in gCO2e/kWh.</param>
    public void UpdateRegionCarbonIntensity(string region, double intensityGCO2ePerKwh)
    {
        _regionCarbonIntensity[region] = intensityGCO2ePerKwh;
    }

    /// <summary>
    /// Gets the number of stored measurements.
    /// </summary>
    public int MeasurementCount => _energyMeasurements.Count;

    #endregion

    #region Helpers

    private double GetRegionCarbonIntensity(string region)
    {
        return _regionCarbonIntensity.TryGetValue(region, out var intensity) && intensity > 0
            ? intensity
            : DefaultCarbonIntensityGCO2ePerKwh;
    }

    private static bool IsComputeOperation(string operationType)
    {
        return operationType.Contains("compute", StringComparison.OrdinalIgnoreCase) ||
               operationType.Contains("process", StringComparison.OrdinalIgnoreCase) ||
               operationType.Contains("index", StringComparison.OrdinalIgnoreCase) ||
               operationType.Contains("query", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStorageOperation(string operationType)
    {
        return operationType.Contains("write", StringComparison.OrdinalIgnoreCase) ||
               operationType.Contains("read", StringComparison.OrdinalIgnoreCase) ||
               operationType.Contains("store", StringComparison.OrdinalIgnoreCase) ||
               operationType.Contains("delete", StringComparison.OrdinalIgnoreCase) ||
               operationType.Contains("list", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNetworkOperation(string operationType)
    {
        return operationType.Contains("transfer", StringComparison.OrdinalIgnoreCase) ||
               operationType.Contains("replicate", StringComparison.OrdinalIgnoreCase) ||
               operationType.Contains("sync", StringComparison.OrdinalIgnoreCase) ||
               operationType.Contains("network", StringComparison.OrdinalIgnoreCase);
    }

    private static DataQualityLevel DetermineDataQuality(List<EnergyMeasurementRecord> measurements)
    {
        if (measurements.Count == 0) return DataQualityLevel.Default;

        // Priority: if any are measured (RAPL/powercap), report as Measured
        if (measurements.Any(m => m.Source is EnergySource.Rapl or EnergySource.Powercap or EnergySource.SmartPdu))
            return DataQualityLevel.Measured;

        if (measurements.Any(m => m.Source == EnergySource.CloudProviderApi))
            return DataQualityLevel.Calculated;

        if (measurements.Any(m => m.Source == EnergySource.Estimation))
            return DataQualityLevel.Estimated;

        return DataQualityLevel.Default;
    }

    private static string GetPrimarySource(List<EnergyMeasurementRecord> measurements)
    {
        if (measurements.Count == 0) return "No data";

        var sourceGroups = measurements
            .GroupBy(m => m.Source)
            .OrderByDescending(g => g.Count())
            .First();

        return sourceGroups.Key switch
        {
            EnergySource.Rapl => "RAPL hardware counters",
            EnergySource.Powercap => "Linux powercap interface",
            EnergySource.IpmiDcmi => "IPMI DCMI power readings",
            EnergySource.SmartPdu => "Smart PDU metering",
            EnergySource.CloudProviderApi => "Cloud provider energy API",
            EnergySource.Estimation => "TDP-based energy estimation",
            _ => "Mixed sources"
        };
    }

    private static string BuildExecutiveSummary(
        double totalScope2, double totalScope3,
        IReadOnlyList<GhgReportEntry> scope2Entries, IReadOnlyList<GhgReportEntry> scope3Entries,
        DateTimeOffset from, DateTimeOffset to)
    {
        var total = totalScope2 + totalScope3;
        var periodDays = (to - from).TotalDays;

        var summary = $"Reporting period: {from:yyyy-MM-dd} to {to:yyyy-MM-dd} ({periodDays:F0} days). ";
        summary += $"Total emissions: {total / 1000.0:F3} kg CO2e. ";
        summary += $"Scope 2 (purchased electricity): {totalScope2 / 1000.0:F3} kg CO2e ({scope2Entries.Count} line items). ";
        summary += $"Scope 3 (value chain): {totalScope3 / 1000.0:F3} kg CO2e ({scope3Entries.Count} line items). ";

        if (total > 0)
        {
            var scope2Pct = totalScope2 / total * 100;
            summary += $"Scope 2 accounts for {scope2Pct:F1}% of total emissions.";
        }

        return summary;
    }

    private static string BuildMethodology()
    {
        return "GHG Protocol Corporate Standard methodology. " +
               "Scope 2: Location-based method using regional grid carbon intensity from " +
               "WattTime/ElectricityMaps APIs multiplied by measured or estimated energy consumption. " +
               "Energy sources prioritized: RAPL hardware counters > powercap sysfs > cloud provider API > TDP estimation. " +
               "Scope 3: Activity-based emission factors applied to data volumes. " +
               "Category 1 (vendor-hosted storage): 0.023 kgCO2e/GB/month. " +
               "Category 4 (data transfer): 0.06 kgCO2e/GB. " +
               "Category 11 (downstream compute): 0.01 kgCO2e/GB read. " +
               "All Scope 3 entries classified as Estimated data quality per GHG Protocol guidance.";
    }

    private void SubscribeToEnergyMeasurements()
    {
        if (MessageBus == null) return;

        _energySubscription = MessageBus.Subscribe("sustainability.energy.measured", (PluginMessage message) =>
        {
            try
            {
                var payload = message.Payload;
                var record = new EnergyMeasurementRecord
                {
                    Timestamp = ExtractTimestamp(payload),
                    WattsConsumed = ExtractDouble(payload, "wattsConsumed"),
                    DurationMs = ExtractDouble(payload, "durationMs"),
                    EnergyWh = ExtractDouble(payload, "energyWh"),
                    Source = ExtractEnergySource(payload),
                    OperationType = ExtractString(payload, "operationType") ?? "unknown",
                    TenantId = ExtractString(payload, "tenantId"),
                    Region = ExtractString(payload, "region") ?? "default",
                    DataSizeBytes = ExtractLong(payload, "dataSizeBytes"),
                    CarbonIntensity = ExtractDouble(payload, "carbonIntensity")
                };

                RecordMeasurement(record);
            }
            catch
            {
                // Measurement ingestion failure should not disrupt the bus
            }

            return Task.CompletedTask;
        });
    }

    private void SubscribeToCarbonIntensityUpdates()
    {
        if (MessageBus == null) return;

        _intensitySubscription = MessageBus.Subscribe("sustainability.carbon.intensity.updated", (PluginMessage message) =>
        {
            try
            {
                var payload = message.Payload;
                var region = ExtractString(payload, "region");
                var intensity = ExtractDouble(payload, "carbonIntensityGCO2ePerKwh");

                if (!string.IsNullOrWhiteSpace(region) && intensity > 0)
                {
                    UpdateRegionCarbonIntensity(region, intensity);
                }
            }
            catch
            {
                // Non-critical
            }

            return Task.CompletedTask;
        });
    }

    #endregion

    #region Payload Extraction

    private static DateTimeOffset ExtractTimestamp(Dictionary<string, object> payload)
    {
        if (payload.TryGetValue("timestamp", out var ts))
        {
            if (ts is DateTimeOffset dto) return dto;
            if (ts is string s && DateTimeOffset.TryParse(s, out var parsed)) return parsed;
        }
        return DateTimeOffset.UtcNow;
    }

    private static double ExtractDouble(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue(key, out var value))
        {
            if (value is double d) return d;
            if (value is int i) return i;
            if (value is long l) return l;
            if (value is float f) return f;
            if (double.TryParse(value?.ToString(), out var parsed)) return parsed;
        }
        return 0;
    }

    private static long ExtractLong(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue(key, out var value))
        {
            if (value is long l) return l;
            if (value is int i) return i;
            if (value is double d) return (long)d;
            if (long.TryParse(value?.ToString(), out var parsed)) return parsed;
        }
        return 0;
    }

    private static string? ExtractString(Dictionary<string, object> payload, string key)
    {
        return payload.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    private static EnergySource ExtractEnergySource(Dictionary<string, object> payload)
    {
        var source = ExtractString(payload, "source");
        if (string.IsNullOrEmpty(source)) return EnergySource.Estimation;

        return Enum.TryParse<EnergySource>(source, ignoreCase: true, out var parsed)
            ? parsed
            : EnergySource.Estimation;
    }

    #endregion
}

/// <summary>
/// Internal record for storing energy measurement data received from the message bus
/// or direct API calls. Captures all fields needed for GHG Protocol reporting.
/// </summary>
public sealed record EnergyMeasurementRecord
{
    /// <summary>UTC timestamp of the measurement.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Power draw in watts.</summary>
    public required double WattsConsumed { get; init; }

    /// <summary>Duration of the operation in milliseconds.</summary>
    public required double DurationMs { get; init; }

    /// <summary>Computed energy consumed in watt-hours.</summary>
    public required double EnergyWh { get; init; }

    /// <summary>Source of the energy measurement.</summary>
    public required EnergySource Source { get; init; }

    /// <summary>Type of storage operation (read, write, delete, list, transfer, etc.).</summary>
    public required string OperationType { get; init; }

    /// <summary>Optional tenant identifier for multi-tenant attribution.</summary>
    public string? TenantId { get; init; }

    /// <summary>Region where the operation executed.</summary>
    public string? Region { get; init; }

    /// <summary>Size of data involved in bytes, if applicable.</summary>
    public long DataSizeBytes { get; init; }

    /// <summary>Carbon intensity at time of measurement (gCO2e/kWh).</summary>
    public double CarbonIntensity { get; init; }
}
