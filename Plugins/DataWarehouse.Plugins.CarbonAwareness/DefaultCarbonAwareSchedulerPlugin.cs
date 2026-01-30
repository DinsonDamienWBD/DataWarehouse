using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Sustainability;

namespace DataWarehouse.Plugins.CarbonAwareness;

/// <summary>
/// Default carbon-aware scheduler implementation.
/// Schedules operations to execute during low-carbon periods based on intensity forecasts.
/// </summary>
public class DefaultCarbonAwareSchedulerPlugin : CarbonAwareSchedulerPluginBase
{
    private readonly ConcurrentDictionary<string, ScheduledOperationState> _scheduledOperations = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _operationCancellations = new();
    private Timer? _schedulerTimer;

    /// <inheritdoc />
    public override string Id => "datawarehouse.carbon.scheduler";

    /// <inheritdoc />
    public override string Name => "Carbon-Aware Scheduler";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.GovernanceProvider;

    /// <summary>
    /// Sets the carbon intensity provider for scheduling decisions.
    /// </summary>
    /// <param name="provider">Carbon intensity provider instance.</param>
    public void SetIntensityProvider(ICarbonIntensityProvider provider)
    {
        IntensityProvider = provider;
    }

    /// <inheritdoc />
    protected override async Task<ScheduledOperation> CreateScheduledOperationAsync(
        string operationId,
        Func<CancellationToken, Task> operation,
        SchedulingOptions options)
    {
        if (IntensityProvider == null)
        {
            throw new InvalidOperationException("Carbon intensity provider not configured");
        }

        // Get forecast for the scheduling window
        var forecast = await IntensityProvider.GetForecastAsync(
            options.RegionId,
            (int)options.MaxDelay.TotalHours + 1);

        // Find optimal execution time within constraints
        var optimalTime = await CalculateOptimalTimeAsync(
            forecast,
            options.MaxDelay,
            DateTimeOffset.UtcNow);

        var estimatedIntensity = forecast.FirstOrDefault(f =>
            f.MeasuredAt <= optimalTime &&
            (f.ForecastedUntil == null || f.ForecastedUntil > optimalTime));

        estimatedIntensity ??= forecast.FirstOrDefault()
            ?? new CarbonIntensityData(options.RegionId, 300, CarbonIntensityLevel.Medium, 30, optimalTime, null);

        var scheduledOp = new ScheduledOperation(
            operationId,
            optimalTime,
            options.AllowMultiRegion ? await FindBestRegionAsync(options.RegionId) : options.RegionId,
            estimatedIntensity,
            OperationStatus.Scheduled
        );

        // Store operation state for execution
        var cts = new CancellationTokenSource();
        _operationCancellations[operationId] = cts;
        _scheduledOperations[operationId] = new ScheduledOperationState(
            scheduledOp,
            operation,
            options,
            cts.Token);

        // Schedule execution
        var delay = optimalTime - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            _ = Task.Delay(delay, cts.Token)
                .ContinueWith(async _ => await ExecuteOperationAsync(operationId), cts.Token);
        }
        else
        {
            // Execute immediately if optimal time is now or past
            _ = ExecuteOperationAsync(operationId);
        }

        return scheduledOp;
    }

    /// <inheritdoc />
    protected override Task<DateTimeOffset> CalculateOptimalTimeAsync(
        IReadOnlyList<CarbonIntensityData> forecast,
        TimeSpan window,
        DateTimeOffset start)
    {
        if (forecast.Count == 0)
        {
            return Task.FromResult(start);
        }

        // Filter forecast to within the scheduling window
        var windowEnd = start + window;
        var windowForecast = forecast
            .Where(f => f.MeasuredAt >= start && f.MeasuredAt <= windowEnd)
            .ToList();

        if (windowForecast.Count == 0)
        {
            // No forecast data in window, execute at start
            return Task.FromResult(start);
        }

        // Find time with lowest carbon intensity
        var optimal = windowForecast.MinBy(f => f.GramsCO2PerKwh);
        return Task.FromResult(optimal?.MeasuredAt ?? start);
    }

    /// <summary>
    /// Cancels a scheduled operation.
    /// </summary>
    /// <param name="operationId">Operation to cancel.</param>
    /// <returns>True if cancelled successfully.</returns>
    public bool CancelOperation(string operationId)
    {
        if (_operationCancellations.TryRemove(operationId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();

            if (_scheduledOperations.TryRemove(operationId, out var state))
            {
                // Update status to cancelled
                _scheduledOperations[operationId] = state with
                {
                    Operation = state.Operation with { Status = OperationStatus.Cancelled }
                };
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the current status of a scheduled operation.
    /// </summary>
    /// <param name="operationId">Operation identifier.</param>
    /// <returns>Current operation status or null if not found.</returns>
    public ScheduledOperation? GetOperationStatus(string operationId)
    {
        return _scheduledOperations.TryGetValue(operationId, out var state)
            ? state.Operation
            : null;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct)
    {
        // Start scheduler timer to check for operations to execute
        _schedulerTimer = new Timer(
            _ => CheckScheduledOperations(),
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task StopAsync()
    {
        _schedulerTimer?.Dispose();
        _schedulerTimer = null;

        // Cancel all pending operations
        foreach (var (id, cts) in _operationCancellations)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _operationCancellations.Clear();
        _scheduledOperations.Clear();

        return Task.CompletedTask;
    }

    private async Task ExecuteOperationAsync(string operationId)
    {
        if (!_scheduledOperations.TryGetValue(operationId, out var state))
            return;

        // Update status to running
        _scheduledOperations[operationId] = state with
        {
            Operation = state.Operation with { Status = OperationStatus.Running }
        };

        try
        {
            await state.Action(state.CancellationToken);

            // Update status to completed
            _scheduledOperations[operationId] = state with
            {
                Operation = state.Operation with { Status = OperationStatus.Completed }
            };
        }
        catch (OperationCanceledException)
        {
            _scheduledOperations[operationId] = state with
            {
                Operation = state.Operation with { Status = OperationStatus.Cancelled }
            };
        }
        catch
        {
            _scheduledOperations[operationId] = state with
            {
                Operation = state.Operation with { Status = OperationStatus.Failed }
            };
        }
        finally
        {
            _operationCancellations.TryRemove(operationId, out _);
        }
    }

    private void CheckScheduledOperations()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var (id, state) in _scheduledOperations)
        {
            if (state.Operation.Status == OperationStatus.Scheduled &&
                state.Operation.ScheduledFor <= now)
            {
                _ = ExecuteOperationAsync(id);
            }
        }
    }

    private async Task<string> FindBestRegionAsync(string preferredRegion)
    {
        if (IntensityProvider == null)
            return preferredRegion;

        var regions = await IntensityProvider.GetAvailableRegionsAsync();
        if (regions.Count == 0)
            return preferredRegion;

        return await IntensityProvider.FindLowestCarbonRegionAsync(regions.ToArray());
    }

    private record ScheduledOperationState(
        ScheduledOperation Operation,
        Func<CancellationToken, Task> Action,
        SchedulingOptions Options,
        CancellationToken CancellationToken);
}
