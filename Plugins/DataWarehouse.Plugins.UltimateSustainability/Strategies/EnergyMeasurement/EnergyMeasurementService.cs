using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Carbon;
using DataWarehouse.SDK.Utilities;
using CarbonEnergyMeasurement = DataWarehouse.SDK.Contracts.Carbon.EnergyMeasurement;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.EnergyMeasurement;

/// <summary>
/// Composite energy measurement service implementing <see cref="IEnergyMeasurementService"/>.
/// Auto-detects the best available measurement source using a cascade:
/// RAPL -> Powercap -> CloudProvider -> Estimation (always available).
/// Stores measurements in a bounded concurrent queue and publishes to the message bus.
/// </summary>
public sealed class EnergyMeasurementService : IEnergyMeasurementService
{
    private const int MaxMeasurements = 100_000;
    private const string PluginId = "com.datawarehouse.sustainability.ultimate";
    private const string MeasurementTopic = "sustainability.energy.measured";

    private readonly ConcurrentQueue<CarbonEnergyMeasurement> _measurements = new();
    private readonly ConcurrentDictionary<string, List<CarbonEnergyMeasurement>> _byOperationType = new();
    private int _measurementCount;

    private readonly RaplEnergyMeasurementStrategy? _raplStrategy;
    private readonly PowercapEnergyMeasurementStrategy? _powercapStrategy;
    private readonly CloudProviderEnergyStrategy? _cloudStrategy;
    private readonly EstimationEnergyStrategy _estimationStrategy;

    private readonly EnergySource _activeSource;
    private readonly IMessageBus? _messageBus;

    /// <summary>
    /// Gets the active energy measurement source detected during initialization.
    /// </summary>
    public EnergySource ActiveSource => _activeSource;

    /// <summary>
    /// Gets the total number of stored measurements.
    /// </summary>
    public int MeasurementCount => _measurementCount;

    /// <summary>
    /// Creates a new EnergyMeasurementService with auto-detection of the best available source.
    /// </summary>
    /// <param name="messageBus">Optional message bus for publishing measurements. Null disables publishing.</param>
    public EnergyMeasurementService(IMessageBus? messageBus = null)
    {
        _messageBus = messageBus;

        // Estimation strategy is always available as ultimate fallback
        _estimationStrategy = new EstimationEnergyStrategy();

        // Auto-detect in priority order: RAPL -> Powercap -> Cloud -> Estimation
        if (RaplEnergyMeasurementStrategy.IsAvailable())
        {
            _raplStrategy = new RaplEnergyMeasurementStrategy();
            _activeSource = EnergySource.Rapl;
        }
        else if (PowercapEnergyMeasurementStrategy.IsAvailable())
        {
            _powercapStrategy = new PowercapEnergyMeasurementStrategy();
            _activeSource = EnergySource.Powercap;
        }
        else if (CloudProviderEnergyStrategy.IsAvailable())
        {
            _cloudStrategy = new CloudProviderEnergyStrategy();
            _activeSource = EnergySource.CloudProviderApi;
        }
        else
        {
            _activeSource = EnergySource.Estimation;
        }
    }

    /// <summary>
    /// Initializes the active measurement strategy.
    /// Must be called before measuring operations.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Always initialize estimation as fallback
        await _estimationStrategy.InitializeAsync(ct);

        // Initialize the active strategy
        switch (_activeSource)
        {
            case EnergySource.Rapl:
                await _raplStrategy!.InitializeAsync(ct);
                break;
            case EnergySource.Powercap:
                await _powercapStrategy!.InitializeAsync(ct);
                break;
            case EnergySource.CloudProviderApi:
                await _cloudStrategy!.InitializeAsync(ct);
                break;
        }
    }

    /// <inheritdoc/>
    public async Task<CarbonEnergyMeasurement> MeasureOperationAsync(
        string operationId,
        string operationType,
        long dataSizeBytes,
        CancellationToken ct = default)
    {
        // Wrap the operation as a no-op since the SDK interface doesn't pass a delegate.
        // The measurement captures current system state for the operation duration.
        var measurement = await MeasureWithActiveStrategyAsync(
            operationId, operationType, dataSizeBytes, tenantId: null, ct);

        StoreMeasurement(measurement);
        await PublishMeasurementAsync(measurement, ct);

        return measurement;
    }

    /// <summary>
    /// Measures energy consumed by a specific async operation, wrapping it for before/after readings.
    /// </summary>
    /// <param name="operationId">Unique identifier for the operation.</param>
    /// <param name="operationType">Type of operation (read, write, delete, list).</param>
    /// <param name="dataSizeBytes">Size of data involved in bytes.</param>
    /// <param name="operation">The async operation to measure.</param>
    /// <param name="tenantId">Optional tenant identifier for attribution.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An EnergyMeasurement record.</returns>
    public async Task<CarbonEnergyMeasurement> MeasureOperationAsync(
        string operationId,
        string operationType,
        long dataSizeBytes,
        Func<Task> operation,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        CarbonEnergyMeasurement measurement;

        try
        {
            measurement = _activeSource switch
            {
                EnergySource.Rapl => await _raplStrategy!.MeasureOperationAsync(
                    operationId, operationType, dataSizeBytes, operation, tenantId, ct),
                EnergySource.Powercap => await _powercapStrategy!.MeasureOperationAsync(
                    operationId, operationType, dataSizeBytes, operation, tenantId, ct),
                EnergySource.CloudProviderApi => await _cloudStrategy!.MeasureOperationAsync(
                    operationId, operationType, dataSizeBytes, operation, tenantId, ct),
                _ => await _estimationStrategy.MeasureOperationAsync(
                    operationId, operationType, dataSizeBytes, operation, tenantId, ct)
            };
        }
        catch
        {
            // If primary strategy fails, fall back to estimation
            measurement = await _estimationStrategy.MeasureOperationAsync(
                operationId, operationType, dataSizeBytes, operation, tenantId, ct);
        }

        StoreMeasurement(measurement);
        await PublishMeasurementAsync(measurement, ct);

        return measurement;
    }

    /// <inheritdoc/>
    public Task<double> GetWattsPerOperationAsync(string operationType, CancellationToken ct = default)
    {
        if (!_byOperationType.TryGetValue(operationType, out var measurements) || measurements.Count == 0)
            return Task.FromResult(0.0);

        double average;
        lock (measurements)
        {
            average = measurements.Average(m => m.WattsConsumed);
        }

        return Task.FromResult(average);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<CarbonEnergyMeasurement>> GetMeasurementsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var filtered = _measurements
            .Where(m => m.Timestamp >= from && m.Timestamp <= to)
            .Where(m => tenantId == null || m.TenantId == tenantId)
            .OrderByDescending(m => m.Timestamp)
            .ToList();

        return Task.FromResult<IReadOnlyList<CarbonEnergyMeasurement>>(filtered.AsReadOnly());
    }

    /// <inheritdoc/>
    public async Task<double> GetCurrentPowerDrawWatts(CancellationToken ct = default)
    {
        try
        {
            return _activeSource switch
            {
                EnergySource.Rapl => await _raplStrategy!.GetCurrentPowerDrawWattsAsync(ct),
                EnergySource.Powercap => await _powercapStrategy!.GetCurrentPowerDrawWattsAsync(ct),
                EnergySource.CloudProviderApi => await _cloudStrategy!.GetCurrentPowerDrawWattsAsync(ct),
                _ => await _estimationStrategy.GetCurrentPowerDrawWattsAsync(ct)
            };
        }
        catch
        {
            // Fallback to estimation
            return await _estimationStrategy.GetCurrentPowerDrawWattsAsync(ct);
        }
    }

    /// <summary>
    /// Measures with the active strategy using a snapshot approach for the SDK interface
    /// (which doesn't pass a delegate).
    /// </summary>
    private async Task<CarbonEnergyMeasurement> MeasureWithActiveStrategyAsync(
        string operationId,
        string operationType,
        long dataSizeBytes,
        string? tenantId,
        CancellationToken ct)
    {
        // For the SDK interface, take a power draw snapshot and estimate based on data size
        var watts = await GetCurrentPowerDrawWatts(ct);

        // Estimate duration from data size (rough: 500 MB/s for SSD, 150 MB/s for HDD)
        var estimatedDurationMs = dataSizeBytes > 0
            ? (dataSizeBytes / (500.0 * 1024 * 1024)) * 1000.0
            : 1.0;

        return new CarbonEnergyMeasurement
        {
            OperationId = operationId,
            Timestamp = DateTimeOffset.UtcNow,
            WattsConsumed = watts,
            DurationMs = estimatedDurationMs,
            Component = EnergyComponent.System,
            Source = _activeSource,
            TenantId = tenantId,
            OperationType = operationType
        };
    }

    private void StoreMeasurement(CarbonEnergyMeasurement measurement)
    {
        _measurements.Enqueue(measurement);
        var count = Interlocked.Increment(ref _measurementCount);

        // Enforce bounded queue
        while (count > MaxMeasurements && _measurements.TryDequeue(out _))
        {
            count = Interlocked.Decrement(ref _measurementCount);
        }

        // Index by operation type for rolling averages
        _byOperationType.AddOrUpdate(
            measurement.OperationType,
            _ => new List<CarbonEnergyMeasurement> { measurement },
            (_, list) =>
            {
                lock (list)
                {
                    list.Add(measurement);
                    // Keep last 10,000 per operation type for averaging
                    if (list.Count > 10_000)
                        list.RemoveAt(0);
                }
                return list;
            });
    }

    private async Task PublishMeasurementAsync(CarbonEnergyMeasurement measurement, CancellationToken ct)
    {
        if (_messageBus == null)
            return;

        try
        {
            var message = new PluginMessage
            {
                Type = "energy.measured",
                SourcePluginId = PluginId,
                Source = "EnergyMeasurementService",
                Description = $"Energy measurement: {measurement.WattsConsumed:F2}W for {measurement.OperationType} operation ({measurement.Source})",
                Payload = new Dictionary<string, object>
                {
                    ["operationId"] = measurement.OperationId,
                    ["operationType"] = measurement.OperationType,
                    ["wattsConsumed"] = measurement.WattsConsumed,
                    ["durationMs"] = measurement.DurationMs,
                    ["energyWh"] = measurement.EnergyWh,
                    ["source"] = measurement.Source.ToString(),
                    ["component"] = measurement.Component.ToString(),
                    ["tenantId"] = measurement.TenantId ?? "system",
                    ["timestamp"] = measurement.Timestamp.ToString("O")
                }
            };

            await _messageBus.PublishAsync(MeasurementTopic, message, ct);
        }
        catch
        {
            // Message bus publish failure is non-fatal for measurement
        }
    }

    /// <summary>
    /// Disposes all strategy resources.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_raplStrategy != null) await _raplStrategy.DisposeAsync();
        if (_powercapStrategy != null) await _powercapStrategy.DisposeAsync();
        if (_cloudStrategy != null) await _cloudStrategy.DisposeAsync();
        await _estimationStrategy.DisposeAsync();
    }
}
