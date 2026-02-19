using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts.Carbon;
using CarbonEnergyMeasurement = DataWarehouse.SDK.Contracts.Carbon.EnergyMeasurement;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.EnergyMeasurement;

/// <summary>
/// Measures energy consumption using the Linux powercap sysfs interface.
/// More generic than RAPL -- works with any powercap-compatible driver including
/// Intel RAPL, AMD RAPL, and ARM energy probes via /sys/class/powercap/*.
/// </summary>
public sealed class PowercapEnergyMeasurementStrategy : SustainabilityStrategyBase
{
    private const string PowercapBasePath = "/sys/class/powercap";
    private const long MaxEnergyUj = (long)uint.MaxValue;

    private readonly ConcurrentDictionary<string, string> _zonePaths = new();
    private string? _primaryZonePath;

    /// <inheritdoc/>
    public override string StrategyId => "powercap-energy-measurement";

    /// <inheritdoc/>
    public override string DisplayName => "Linux Powercap Energy Measurement";

    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.EnergyOptimization;

    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.RealTimeMonitoring |
        SustainabilityCapabilities.CarbonCalculation;

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Measures energy consumption using the Linux powercap sysfs interface. " +
        "Supports any powercap-compatible driver including Intel RAPL, AMD RAPL, and ARM energy probes.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "energy", "measurement", "powercap", "sysfs", "linux", "amd", "arm", "power"
    };

    /// <summary>
    /// Checks whether powercap energy measurement is available on this system.
    /// Requires Linux OS and the /sys/class/powercap directory with at least one zone.
    /// </summary>
    public static bool IsAvailable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        if (!Directory.Exists(PowercapBasePath))
            return false;

        try
        {
            // Must have at least one powercap zone with energy_uj
            var baseDir = new DirectoryInfo(PowercapBasePath);
            return baseDir.GetDirectories().Any(d =>
                File.Exists(Path.Combine(d.FullName, "energy_uj")));
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        DiscoverPowercapZones();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Measures the energy consumed by a storage operation using powercap counters.
    /// </summary>
    /// <param name="operationId">Unique identifier for this operation.</param>
    /// <param name="operationType">Type of operation (read, write, delete, list).</param>
    /// <param name="dataSizeBytes">Size of data involved in bytes.</param>
    /// <param name="operation">The async operation to measure.</param>
    /// <param name="tenantId">Optional tenant identifier for attribution.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An EnergyMeasurement record with watts consumed and duration.</returns>
    public async Task<CarbonEnergyMeasurement> MeasureOperationAsync(
        string operationId,
        string operationType,
        long dataSizeBytes,
        Func<Task> operation,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (_primaryZonePath == null)
            throw new InvalidOperationException("No powercap zones available for measurement.");

        // Read aggregate energy before operation
        var beforeUj = await ReadAggregateEnergyAsync(ct);
        var sw = Stopwatch.StartNew();

        // Execute the measured operation
        await operation();

        sw.Stop();
        // Read aggregate energy after operation
        var afterUj = await ReadAggregateEnergyAsync(ct);

        var deltaUj = ComputeEnergyDelta(beforeUj, afterUj);
        var durationMs = sw.Elapsed.TotalMilliseconds;

        // Convert microjoules to watts
        var watts = durationMs > 0
            ? (deltaUj / 1_000_000.0) / (durationMs / 1_000.0)
            : 0.0;

        RecordSample(watts, carbonIntensity: 0);

        return new CarbonEnergyMeasurement
        {
            OperationId = operationId,
            Timestamp = DateTimeOffset.UtcNow,
            WattsConsumed = watts,
            DurationMs = durationMs,
            Component = EnergyComponent.System,
            Source = EnergySource.Powercap,
            TenantId = tenantId,
            OperationType = operationType
        };
    }

    /// <summary>
    /// Reads the current instantaneous power draw from powercap counters.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current power draw in watts.</returns>
    public async Task<double> GetCurrentPowerDrawWattsAsync(CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (_primaryZonePath == null)
            return 0.0;

        var beforeUj = await ReadAggregateEnergyAsync(ct);
        var sw = Stopwatch.StartNew();
        await Task.Delay(100, ct);
        sw.Stop();
        var afterUj = await ReadAggregateEnergyAsync(ct);

        var deltaUj = ComputeEnergyDelta(beforeUj, afterUj);
        var durationSeconds = sw.Elapsed.TotalSeconds;

        return durationSeconds > 0 ? (deltaUj / 1_000_000.0) / durationSeconds : 0.0;
    }

    /// <summary>
    /// Gets all discovered powercap zones and their names.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetDiscoveredZones()
    {
        return new Dictionary<string, string>(_zonePaths);
    }

    private void DiscoverPowercapZones()
    {
        _zonePaths.Clear();
        _primaryZonePath = null;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        try
        {
            var baseDir = new DirectoryInfo(PowercapBasePath);
            if (!baseDir.Exists)
                return;

            foreach (var zoneDir in baseDir.GetDirectories())
            {
                var energyFile = Path.Combine(zoneDir.FullName, "energy_uj");
                if (!File.Exists(energyFile))
                    continue;

                var nameFile = Path.Combine(zoneDir.FullName, "name");
                var zoneName = File.Exists(nameFile)
                    ? File.ReadAllText(nameFile).Trim()
                    : zoneDir.Name;

                _zonePaths[zoneName] = energyFile;

                // Use first package-level zone as primary
                if (_primaryZonePath == null && IsPackageLevelZone(zoneName, zoneDir.Name))
                    _primaryZonePath = energyFile;
            }

            // If no package-level zone found, use the first available zone
            _primaryZonePath ??= _zonePaths.Values.FirstOrDefault();
        }
        catch (UnauthorizedAccessException)
        {
            // Insufficient permissions to enumerate powercap zones
        }
        catch (IOException)
        {
            // Sysfs enumeration failure
        }
    }

    private static bool IsPackageLevelZone(string zoneName, string dirName)
    {
        // Intel RAPL package zones
        if (zoneName.Contains("package", StringComparison.OrdinalIgnoreCase))
            return true;

        // AMD RAPL zones use similar naming
        if (dirName.StartsWith("intel-rapl:", StringComparison.OrdinalIgnoreCase) &&
            !dirName.Contains(":0:", StringComparison.Ordinal))
            return true;

        return false;
    }

    /// <summary>
    /// Reads aggregate energy from all package-level powercap zones.
    /// Sums readings from multiple CPU packages on multi-socket systems.
    /// </summary>
    private async Task<long> ReadAggregateEnergyAsync(CancellationToken ct)
    {
        long totalUj = 0;

        foreach (var (zoneName, path) in _zonePaths)
        {
            // Only aggregate package-level zones to avoid double-counting sub-domains
            if (!IsPackageLevelZone(zoneName, Path.GetFileName(Path.GetDirectoryName(path) ?? "")))
                continue;

            try
            {
                var text = await File.ReadAllTextAsync(path, ct);
                totalUj += long.Parse(text.Trim());
            }
            catch (FileNotFoundException)
            {
                // Zone removed during operation -- skip
            }
            catch (UnauthorizedAccessException)
            {
                // Permission lost -- skip
            }
        }

        // Fallback to primary zone if aggregate is zero
        if (totalUj == 0 && _primaryZonePath != null)
        {
            try
            {
                var text = await File.ReadAllTextAsync(_primaryZonePath, ct);
                totalUj = long.Parse(text.Trim());
            }
            catch
            {
                throw new InvalidOperationException("Failed to read any powercap energy counter.");
            }
        }

        return totalUj;
    }

    private static long ComputeEnergyDelta(long before, long after)
    {
        if (after >= before)
            return after - before;

        // Counter overflow
        return (MaxEnergyUj - before) + after;
    }
}
