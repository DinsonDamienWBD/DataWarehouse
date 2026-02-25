using System.Diagnostics;
using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts.Carbon;
using CarbonEnergyMeasurement = DataWarehouse.SDK.Contracts.Carbon.EnergyMeasurement;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.EnergyMeasurement;

/// <summary>
/// TDP-based energy estimation fallback strategy. Always available on any platform.
/// Uses processor architecture detection, TDP modeling, and I/O energy models to
/// estimate watts consumed per storage operation without hardware counter access.
/// </summary>
public sealed class EstimationEnergyStrategy : SustainabilityStrategyBase
{
    /// <summary>
    /// Idle power ratio relative to TDP. Typical CPUs consume ~30% of TDP at idle.
    /// </summary>
    private const double IdleFraction = 0.30;

    /// <summary>
    /// Storage I/O energy models (watt-hours per gigabyte).
    /// Based on published SSD/HDD power consumption measurements.
    /// </summary>
    private static readonly Dictionary<string, double> StorageIoEnergyWhPerGb = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ssd_read"] = 0.003,
        ["ssd_write"] = 0.005,
        ["hdd_read"] = 0.010,
        ["hdd_write"] = 0.015,
        ["nvme_read"] = 0.002,
        ["nvme_write"] = 0.004,
    };

    /// <summary>
    /// Network I/O energy model: ~0.001 Wh per GB.
    /// </summary>
    private const double NetworkIoWhPerGb = 0.001;

    private double _baseTdpWatts;
    private TimeSpan _lastCpuTime;
    private DateTimeOffset _lastCpuTimeStamp;

    /// <inheritdoc/>
    public override string StrategyId => "estimation-energy";

    /// <inheritdoc/>
    public override string DisplayName => "TDP-Based Energy Estimation";

    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.EnergyOptimization;

    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.CarbonCalculation;

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Estimates energy consumption using TDP-based power modeling. " +
        "Always available as a universal fallback when hardware counters and cloud APIs are not accessible. " +
        "Uses CPU architecture detection, process CPU utilization, and storage I/O energy models.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "energy", "measurement", "estimation", "tdp", "fallback", "universal"
    };

    /// <summary>
    /// Estimation strategy is always available -- it is the universal fallback.
    /// </summary>
    public static bool IsAvailable() => true;

    /// <summary>
    /// Gets the estimated base TDP in watts for the detected processor.
    /// </summary>
    public double BaseTdpWatts => _baseTdpWatts;

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _baseTdpWatts = DetectTdp();

        var process = Process.GetCurrentProcess();
        _lastCpuTime = process.TotalProcessorTime;
        _lastCpuTimeStamp = DateTimeOffset.UtcNow;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Measures the energy consumed by a storage operation using TDP-based estimation.
    /// </summary>
    public async Task<CarbonEnergyMeasurement> MeasureOperationAsync(
        string operationId,
        string operationType,
        long dataSizeBytes,
        Func<Task> operation,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        // Capture CPU time before operation
        var process = Process.GetCurrentProcess();
        var cpuTimeBefore = process.TotalProcessorTime;
        var sw = Stopwatch.StartNew();

        await operation();

        sw.Stop();
        process.Refresh();
        var cpuTimeAfter = process.TotalProcessorTime;

        var durationMs = sw.Elapsed.TotalMilliseconds;
        var cpuDeltaMs = (cpuTimeAfter - cpuTimeBefore).TotalMilliseconds;
        var wallDeltaMs = durationMs;

        // Calculate CPU utilization for this operation
        var cpuUtilization = wallDeltaMs > 0
            ? Math.Clamp(cpuDeltaMs / (wallDeltaMs * Environment.ProcessorCount), 0.0, 1.0)
            : 0.0;

        // TDP power model: watts = baseTdp * (idleFraction + (1 - idleFraction) * cpuUtilization)
        var cpuWatts = _baseTdpWatts * (IdleFraction + (1.0 - IdleFraction) * cpuUtilization);

        // Add storage I/O energy contribution
        var storageWatts = EstimateStorageIoPower(operationType, dataSizeBytes, durationMs);

        var totalWatts = cpuWatts + storageWatts;

        // Update CPU tracking
        _lastCpuTime = cpuTimeAfter;
        _lastCpuTimeStamp = DateTimeOffset.UtcNow;

        RecordSample(totalWatts, carbonIntensity: 0);

        return new CarbonEnergyMeasurement
        {
            OperationId = operationId,
            Timestamp = DateTimeOffset.UtcNow,
            WattsConsumed = totalWatts,
            DurationMs = durationMs,
            Component = EnergyComponent.System,
            Source = EnergySource.Estimation,
            TenantId = tenantId,
            OperationType = operationType
        };
    }

    /// <summary>
    /// Gets current estimated power draw in watts.
    /// </summary>
    public Task<double> GetCurrentPowerDrawWattsAsync(CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var process = Process.GetCurrentProcess();
        var now = DateTimeOffset.UtcNow;
        var cpuTimeNow = process.TotalProcessorTime;

        var wallDelta = (now - _lastCpuTimeStamp).TotalMilliseconds;
        var cpuDelta = (cpuTimeNow - _lastCpuTime).TotalMilliseconds;

        var cpuUtilization = wallDelta > 0
            ? Math.Clamp(cpuDelta / (wallDelta * Environment.ProcessorCount), 0.0, 1.0)
            : 0.0;

        var watts = _baseTdpWatts * (IdleFraction + (1.0 - IdleFraction) * cpuUtilization);

        _lastCpuTime = cpuTimeNow;
        _lastCpuTimeStamp = now;

        return Task.FromResult(watts);
    }

    /// <summary>
    /// Estimates the storage I/O power contribution for an operation.
    /// </summary>
    /// <param name="operationType">Type of operation (read/write).</param>
    /// <param name="dataSizeBytes">Size of data in bytes.</param>
    /// <param name="durationMs">Duration of the operation in milliseconds.</param>
    /// <returns>Estimated additional watts from storage I/O.</returns>
    private static double EstimateStorageIoPower(string operationType, long dataSizeBytes, double durationMs)
    {
        if (dataSizeBytes <= 0 || durationMs <= 0)
            return 0.0;

        var dataSizeGb = dataSizeBytes / (1024.0 * 1024.0 * 1024.0);
        var durationHours = durationMs / 3_600_000.0;

        // Default to SSD model (most common modern storage)
        var whPerGb = operationType.Contains("write", StringComparison.OrdinalIgnoreCase)
            ? StorageIoEnergyWhPerGb["ssd_write"]
            : StorageIoEnergyWhPerGb["ssd_read"];

        var totalWh = dataSizeGb * whPerGb;

        // Convert to average watts over the operation duration
        return durationHours > 0 ? totalWh / durationHours : 0.0;
    }

    /// <summary>
    /// Detects TDP based on processor architecture and count.
    /// </summary>
    private static double DetectTdp()
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        var cpuCount = Environment.ProcessorCount;

        return arch switch
        {
            // ARM64 (Graviton, Apple Silicon, Ampere): ~15W base for efficient processors
            Architecture.Arm64 => cpuCount switch
            {
                <= 4 => 15.0,
                <= 8 => 25.0,
                <= 16 => 40.0,
                <= 64 => 80.0,
                _ => cpuCount * 1.5
            },
            // ARM (32-bit): embedded/mobile, very low power
            Architecture.Arm => cpuCount switch
            {
                <= 2 => 5.0,
                <= 4 => 10.0,
                _ => cpuCount * 2.5
            },
            // x64: Intel/AMD desktop and server processors
            _ => cpuCount switch
            {
                <= 4 => 65.0,   // Desktop-class
                <= 8 => 95.0,   // Mid-range
                <= 16 => 125.0, // Server single-socket
                <= 32 => 180.0, // Server dual-socket
                <= 64 => 280.0, // High-end server
                _ => cpuCount * 5.0 // Scale linearly for very large systems
            }
        };
    }
}
