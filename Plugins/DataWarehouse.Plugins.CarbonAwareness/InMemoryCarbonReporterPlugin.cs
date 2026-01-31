using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Sustainability;

namespace DataWarehouse.Plugins.CarbonAwareness;

/// <summary>
/// In-memory carbon usage reporter for tracking and reporting emissions.
/// Provides storage, aggregation, and reporting of carbon footprint data.
/// </summary>
public class InMemoryCarbonReporterPlugin : CarbonReporterPluginBase
{
    private readonly ConcurrentBag<CarbonUsageRecord> _usageRecords = new();
    private readonly object _reportLock = new();

    /// <inheritdoc />
    public override string Id => "datawarehouse.carbon.reporter.inmemory";

    /// <inheritdoc />
    public override string Name => "In-Memory Carbon Reporter";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.GovernanceProvider;

    /// <inheritdoc />
    protected override Task StoreUsageAsync(CarbonUsageRecord usage)
    {
        _usageRecords.Add(usage);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyList<CarbonUsageRecord>> GetUsageRecordsAsync(
        DateTimeOffset start,
        DateTimeOffset end)
    {
        var records = _usageRecords
            .Where(r => r.Timestamp >= start && r.Timestamp <= end)
            .OrderBy(r => r.Timestamp)
            .ToList();

        return Task.FromResult<IReadOnlyList<CarbonUsageRecord>>(records);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<CarbonReportEntry> GroupByGranularity(
        IReadOnlyList<CarbonUsageRecord> records,
        ReportGranularity granularity)
    {
        if (records.Count == 0)
            return Array.Empty<CarbonReportEntry>();

        var grouped = granularity switch
        {
            ReportGranularity.Hourly => GroupByHour(records),
            ReportGranularity.Daily => GroupByDay(records),
            ReportGranularity.Weekly => GroupByWeek(records),
            ReportGranularity.Monthly => GroupByMonth(records),
            _ => GroupByDay(records)
        };

        return grouped;
    }

    /// <summary>
    /// Gets all recorded carbon usage data.
    /// </summary>
    /// <returns>All usage records.</returns>
    public IReadOnlyList<CarbonUsageRecord> GetAllRecords()
    {
        return _usageRecords.ToList();
    }

    /// <summary>
    /// Clears all stored usage records.
    /// </summary>
    public void ClearRecords()
    {
        lock (_reportLock)
        {
            _usageRecords.Clear();
        }
    }

    /// <summary>
    /// Gets carbon usage summary by operation type.
    /// </summary>
    /// <returns>Dictionary of operation type to total carbon grams.</returns>
    public Dictionary<string, double> GetUsageByOperationType()
    {
        return _usageRecords
            .GroupBy(r => r.OperationType)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.CarbonGrams));
    }

    /// <summary>
    /// Gets carbon usage summary by region.
    /// </summary>
    /// <returns>Dictionary of region to total carbon grams.</returns>
    public Dictionary<string, double> GetUsageByRegion()
    {
        return _usageRecords
            .GroupBy(r => r.RegionId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.CarbonGrams));
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => Task.CompletedTask;

    private static IReadOnlyList<CarbonReportEntry> GroupByHour(IReadOnlyList<CarbonUsageRecord> records)
    {
        return records
            .GroupBy(r => new { Hour = new DateTimeOffset(r.Timestamp.Year, r.Timestamp.Month, r.Timestamp.Day, r.Timestamp.Hour, 0, 0, r.Timestamp.Offset), r.RegionId })
            .Select(g => new CarbonReportEntry(
                g.Key.Hour,
                g.Key.RegionId,
                g.Sum(r => r.CarbonGrams),
                g.Sum(r => r.EnergyKwh),
                0.0 // Renewable percentage would need intensity data
            ))
            .OrderBy(e => e.Period)
            .ThenBy(e => e.RegionId)
            .ToList();
    }

    private static IReadOnlyList<CarbonReportEntry> GroupByDay(IReadOnlyList<CarbonUsageRecord> records)
    {
        return records
            .GroupBy(r => new { Day = new DateTimeOffset(r.Timestamp.Year, r.Timestamp.Month, r.Timestamp.Day, 0, 0, 0, r.Timestamp.Offset), r.RegionId })
            .Select(g => new CarbonReportEntry(
                g.Key.Day,
                g.Key.RegionId,
                g.Sum(r => r.CarbonGrams),
                g.Sum(r => r.EnergyKwh),
                0.0
            ))
            .OrderBy(e => e.Period)
            .ThenBy(e => e.RegionId)
            .ToList();
    }

    private static IReadOnlyList<CarbonReportEntry> GroupByWeek(IReadOnlyList<CarbonUsageRecord> records)
    {
        return records
            .GroupBy(r => new {
                Week = new DateTimeOffset(r.Timestamp.Year, r.Timestamp.Month, r.Timestamp.Day, 0, 0, 0, r.Timestamp.Offset)
                    .AddDays(-(int)r.Timestamp.DayOfWeek),
                r.RegionId
            })
            .Select(g => new CarbonReportEntry(
                g.Key.Week,
                g.Key.RegionId,
                g.Sum(r => r.CarbonGrams),
                g.Sum(r => r.EnergyKwh),
                0.0
            ))
            .OrderBy(e => e.Period)
            .ThenBy(e => e.RegionId)
            .ToList();
    }

    private static IReadOnlyList<CarbonReportEntry> GroupByMonth(IReadOnlyList<CarbonUsageRecord> records)
    {
        return records
            .GroupBy(r => new { Month = new DateTimeOffset(r.Timestamp.Year, r.Timestamp.Month, 1, 0, 0, 0, r.Timestamp.Offset), r.RegionId })
            .Select(g => new CarbonReportEntry(
                g.Key.Month,
                g.Key.RegionId,
                g.Sum(r => r.CarbonGrams),
                g.Sum(r => r.EnergyKwh),
                0.0
            ))
            .OrderBy(e => e.Period)
            .ThenBy(e => e.RegionId)
            .ToList();
    }
}
