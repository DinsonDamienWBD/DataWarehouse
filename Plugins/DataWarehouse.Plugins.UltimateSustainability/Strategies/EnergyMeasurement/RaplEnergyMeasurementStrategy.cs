using System.Diagnostics;
using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts.Carbon;
using CarbonEnergyMeasurement = DataWarehouse.SDK.Contracts.Carbon.EnergyMeasurement;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.EnergyMeasurement;

/// <summary>
/// Measures energy consumption using Intel RAPL (Running Average Power Limit) hardware counters.
/// Reads microjoule counters from /sys/class/powercap/intel-rapl on Linux to obtain
/// ground-truth power measurements for storage operations.
/// </summary>
public sealed class RaplEnergyMeasurementStrategy : SustainabilityStrategyBase
{
    private const string RaplBasePath = "/sys/class/powercap/intel-rapl";
    private const string PackagePath = "/sys/class/powercap/intel-rapl/intel-rapl:0";
    private const long MaxEnergyUj = (long)uint.MaxValue; // 32-bit overflow boundary

    private readonly BoundedDictionary<string, double> _domainReadings = new BoundedDictionary<string, double>(1000);
    private readonly BoundedDictionary<string, string> _domainPaths = new BoundedDictionary<string, string>(1000);

    /// <inheritdoc/>
    public override string StrategyId => "rapl-energy-measurement";

    /// <inheritdoc/>
    public override string DisplayName => "Intel RAPL Energy Measurement";

    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.EnergyOptimization;

    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.RealTimeMonitoring |
        SustainabilityCapabilities.CarbonCalculation;

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Measures energy consumption using Intel RAPL hardware counters via Linux powercap sysfs. " +
        "Provides ground-truth watt measurements per storage operation on Intel and AMD processors.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "energy", "measurement", "rapl", "intel", "hardware", "power", "watt"
    };

    /// <summary>
    /// Checks whether RAPL energy measurement is available on this system.
    /// Requires Linux OS and the intel-rapl powercap directory to exist.
    /// </summary>
    public static bool IsAvailable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        return Directory.Exists(RaplBasePath);
    }

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        DiscoverRaplDomains();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Measures the energy consumed by a storage operation using RAPL counters.
    /// Reads the energy_uj counter before and after the operation to compute delta.
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

        var packageEnergyPath = GetPackageEnergyPath();
        if (packageEnergyPath == null)
            throw new InvalidOperationException("RAPL energy counter not available.");

        // Read energy counter before operation
        var beforeUj = await ReadEnergyCounterAsync(packageEnergyPath, ct);
        var sw = Stopwatch.StartNew();

        // Execute the measured operation
        await operation();

        sw.Stop();
        // Read energy counter after operation
        var afterUj = await ReadEnergyCounterAsync(packageEnergyPath, ct);

        // Compute energy delta, handling 32-bit counter overflow
        var deltaUj = ComputeEnergyDelta(beforeUj, afterUj);
        var durationMs = sw.Elapsed.TotalMilliseconds;

        // Convert microjoules to watts: watts = (deltaUj / 1e6) / (durationMs / 1000)
        var watts = durationMs > 0
            ? (deltaUj / 1_000_000.0) / (durationMs / 1_000.0)
            : 0.0;

        // Update per-domain readings
        await UpdateDomainReadingsAsync(ct);

        // Record sample for statistics tracking
        RecordSample(watts, carbonIntensity: 0);

        return new CarbonEnergyMeasurement
        {
            OperationId = operationId,
            Timestamp = DateTimeOffset.UtcNow,
            WattsConsumed = watts,
            DurationMs = durationMs,
            Component = EnergyComponent.System,
            Source = EnergySource.Rapl,
            TenantId = tenantId,
            OperationType = operationType
        };
    }

    /// <summary>
    /// Reads the current instantaneous power draw from RAPL counters.
    /// Takes two readings 100ms apart to compute power.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current power draw in watts.</returns>
    public async Task<double> GetCurrentPowerDrawWattsAsync(CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var packageEnergyPath = GetPackageEnergyPath();
        if (packageEnergyPath == null)
            return 0.0;

        var beforeUj = await ReadEnergyCounterAsync(packageEnergyPath, ct);
        var sw = Stopwatch.StartNew();
        await Task.Delay(100, ct);
        sw.Stop();
        var afterUj = await ReadEnergyCounterAsync(packageEnergyPath, ct);

        var deltaUj = ComputeEnergyDelta(beforeUj, afterUj);
        var durationSeconds = sw.Elapsed.TotalSeconds;

        return durationSeconds > 0 ? (deltaUj / 1_000_000.0) / durationSeconds : 0.0;
    }

    /// <summary>
    /// Gets the latest per-domain power readings (package, core, uncore, dram).
    /// </summary>
    public IReadOnlyDictionary<string, double> GetDomainReadings()
    {
        return new Dictionary<string, double>(_domainReadings);
    }

    private void DiscoverRaplDomains()
    {
        _domainPaths.Clear();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        // Package-level domain
        var packageEnergy = $"{PackagePath}/energy_uj";
        if (File.Exists(packageEnergy))
            _domainPaths["package"] = packageEnergy;

        // Scan for sub-domains: core, uncore, dram
        try
        {
            var packageDir = new DirectoryInfo(PackagePath);
            if (!packageDir.Exists)
                return;

            foreach (var subDir in packageDir.GetDirectories("intel-rapl:0:*"))
            {
                var energyFile = Path.Combine(subDir.FullName, "energy_uj");
                var nameFile = Path.Combine(subDir.FullName, "name");

                if (!File.Exists(energyFile))
                    continue;

                var domainName = File.Exists(nameFile)
                    ? File.ReadAllText(nameFile).Trim()
                    : subDir.Name;

                _domainPaths[domainName] = energyFile;
            }
        }
        catch (UnauthorizedAccessException ex)
        {

            // Insufficient permissions to enumerate RAPL sub-domains
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
        catch (IOException ex)
        {

            // Sysfs enumeration failure
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
    }

    private string? GetPackageEnergyPath()
    {
        return _domainPaths.TryGetValue("package", out var path) ? path : null;
    }

    private static async Task<long> ReadEnergyCounterAsync(string path, CancellationToken ct)
    {
        try
        {
            var text = await File.ReadAllTextAsync(path, ct);
            return long.Parse(text.Trim());
        }
        catch (FileNotFoundException)
        {
            throw new InvalidOperationException($"RAPL energy counter not found at {path}. The counter may have been removed or permissions changed.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Insufficient permissions to read RAPL counter at {path}. Run as root or add read permissions.");
        }
    }

    /// <summary>
    /// Computes energy delta handling 32-bit counter overflow.
    /// RAPL energy counters wrap at approximately 2^32 microjoules.
    /// </summary>
    private static long ComputeEnergyDelta(long before, long after)
    {
        if (after >= before)
            return after - before;

        // Counter overflow: add max value to compensate
        return (MaxEnergyUj - before) + after;
    }

    private async Task UpdateDomainReadingsAsync(CancellationToken ct)
    {
        foreach (var (domain, path) in _domainPaths)
        {
            try
            {
                var beforeUj = await ReadEnergyCounterAsync(path, ct);
                await Task.Delay(50, ct);
                var afterUj = await ReadEnergyCounterAsync(path, ct);

                var deltaUj = ComputeEnergyDelta(beforeUj, afterUj);
                var watts = (deltaUj / 1_000_000.0) / 0.05; // 50ms sample

                _domainReadings[domain] = watts;
            }
            catch
            {

                // Individual domain read failure is non-fatal
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }
    }
}
