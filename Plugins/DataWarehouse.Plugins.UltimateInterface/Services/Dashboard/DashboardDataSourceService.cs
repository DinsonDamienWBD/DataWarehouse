using System.Collections.Concurrent;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Dashboards;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateInterface.Dashboards.Strategies;

/// <summary>
/// Dashboard data source abstraction layer. Provides multiple data source implementations
/// including bus topic subscription, query execution, and static data sources.
/// Supports real-time push updates via bus subscription notifications.
/// </summary>
public sealed class DashboardDataSourceService
{
    private readonly BoundedDictionary<string, IDashboardDataSource> _dataSources = new BoundedDictionary<string, IDashboardDataSource>(1000);
    private readonly ConcurrentDictionary<string, List<Action<DataSourceUpdate>>> _subscribers = new();

    /// <summary>
    /// Registers a data source.
    /// </summary>
    public void RegisterDataSource(IDashboardDataSource dataSource)
    {
        _dataSources[dataSource.DataSourceId] = dataSource;
    }

    /// <summary>
    /// Gets a data source by ID.
    /// </summary>
    public IDashboardDataSource? GetDataSource(string dataSourceId) =>
        _dataSources.TryGetValue(dataSourceId, out var ds) ? ds : null;

    /// <summary>
    /// Executes a query against a data source and returns results.
    /// </summary>
    public async Task<DataSourceResult> QueryAsync(string dataSourceId, string query,
        TimeRange? timeRange = null, Dictionary<string, object>? parameters = null,
        CancellationToken ct = default)
    {
        if (!_dataSources.TryGetValue(dataSourceId, out var dataSource))
            return new DataSourceResult { Success = false, ErrorMessage = $"Data source '{dataSourceId}' not found" };

        return await dataSource.ExecuteQueryAsync(query, timeRange, parameters, ct);
    }

    /// <summary>
    /// Subscribes to real-time updates from a data source.
    /// </summary>
    public string Subscribe(string dataSourceId, Action<DataSourceUpdate> callback)
    {
        var subscriptionId = Guid.NewGuid().ToString("N");
        var subscribers = _subscribers.GetOrAdd(dataSourceId, _ => new List<Action<DataSourceUpdate>>());
        lock (subscribers) { subscribers.Add(callback); }
        return subscriptionId;
    }

    /// <summary>
    /// Pushes an update to all subscribers of a data source.
    /// </summary>
    public void PushUpdate(string dataSourceId, DataSourceUpdate update)
    {
        if (!_subscribers.TryGetValue(dataSourceId, out var subscribers)) return;
        lock (subscribers)
        {
            foreach (var callback in subscribers)
            {
                try { callback(update); }
                catch { /* Don't let subscriber errors propagate */ }
            }
        }
    }

    /// <summary>
    /// Lists all registered data sources.
    /// </summary>
    public IReadOnlyList<DataSourceInfo> ListDataSources() =>
        _dataSources.Values.Select(ds => new DataSourceInfo
        {
            DataSourceId = ds.DataSourceId,
            Name = ds.Name,
            Type = ds.Type,
            SupportsRealTime = ds.SupportsRealTime
        }).ToList().AsReadOnly();
}

/// <summary>
/// Interface for dashboard data sources.
/// </summary>
public interface IDashboardDataSource
{
    string DataSourceId { get; }
    string Name { get; }
    string Type { get; }
    bool SupportsRealTime { get; }
    Task<DataSourceResult> ExecuteQueryAsync(string query, TimeRange? timeRange,
        Dictionary<string, object>? parameters, CancellationToken ct);
}

/// <summary>
/// Bus topic data source that subscribes to message bus observability topics.
/// </summary>
public sealed class BusTopicDataSource : IDashboardDataSource
{
    private readonly ConcurrentDictionary<string, List<Dictionary<string, object>>> _topicData = new();

    public string DataSourceId { get; }
    public string Name { get; }
    public string Type => "BusTopic";
    public bool SupportsRealTime => true;
    public string TopicPattern { get; }

    public BusTopicDataSource(string id, string name, string topicPattern)
    {
        DataSourceId = id;
        Name = name;
        TopicPattern = topicPattern;
    }

    /// <summary>
    /// Ingests data from a bus topic message.
    /// </summary>
    public void IngestMessage(string topic, Dictionary<string, object> data)
    {
        var buffer = _topicData.GetOrAdd(topic, _ => new List<Dictionary<string, object>>());
        lock (buffer)
        {
            buffer.Add(data);
            if (buffer.Count > 10000) buffer.RemoveAt(0); // Ring buffer
        }
    }

    public Task<DataSourceResult> ExecuteQueryAsync(string query, TimeRange? timeRange,
        Dictionary<string, object>? parameters, CancellationToken ct)
    {
        var allData = _topicData.Values
            .SelectMany(list => { lock (list) { return list.ToList(); } })
            .ToList();

        // Apply time range filter if data has timestamps
        if (timeRange != null && timeRange.From.HasValue)
        {
            allData = allData.Where(d =>
            {
                if (d.TryGetValue("timestamp", out var ts) && ts is DateTimeOffset dto)
                    return dto >= timeRange.From.Value;
                return true;
            }).ToList();
        }

        return Task.FromResult(new DataSourceResult
        {
            Success = true,
            Data = allData,
            TotalCount = allData.Count,
            DataSourceId = DataSourceId
        });
    }
}

/// <summary>
/// Query data source that executes structured queries against stored data.
/// </summary>
public sealed class QueryDataSource : IDashboardDataSource
{
    private readonly Func<string, Dictionary<string, object>?, Task<List<Dictionary<string, object>>>> _queryExecutor;

    public string DataSourceId { get; }
    public string Name { get; }
    public string Type => "Query";
    public bool SupportsRealTime => false;

    public QueryDataSource(string id, string name,
        Func<string, Dictionary<string, object>?, Task<List<Dictionary<string, object>>>> queryExecutor)
    {
        DataSourceId = id;
        Name = name;
        _queryExecutor = queryExecutor;
    }

    public async Task<DataSourceResult> ExecuteQueryAsync(string query, TimeRange? timeRange,
        Dictionary<string, object>? parameters, CancellationToken ct)
    {
        try
        {
            var data = await _queryExecutor(query, parameters);
            return new DataSourceResult
            {
                Success = true,
                Data = data,
                TotalCount = data.Count,
                DataSourceId = DataSourceId
            };
        }
        catch (Exception ex)
        {
            return new DataSourceResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                DataSourceId = DataSourceId
            };
        }
    }
}

/// <summary>
/// Static data source with pre-configured values.
/// </summary>
public sealed class StaticDataSource : IDashboardDataSource
{
    private readonly List<Dictionary<string, object>> _data;

    public string DataSourceId { get; }
    public string Name { get; }
    public string Type => "Static";
    public bool SupportsRealTime => false;

    public StaticDataSource(string id, string name, List<Dictionary<string, object>> data)
    {
        DataSourceId = id;
        Name = name;
        _data = data;
    }

    public Task<DataSourceResult> ExecuteQueryAsync(string query, TimeRange? timeRange,
        Dictionary<string, object>? parameters, CancellationToken ct)
    {
        return Task.FromResult(new DataSourceResult
        {
            Success = true,
            Data = _data,
            TotalCount = _data.Count,
            DataSourceId = DataSourceId
        });
    }
}

#region Models

public sealed record DataSourceResult
{
    public bool Success { get; init; }
    public List<Dictionary<string, object>> Data { get; init; } = new();
    public int TotalCount { get; init; }
    public string? DataSourceId { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record DataSourceUpdate
{
    public required string DataSourceId { get; init; }
    public required string UpdateType { get; init; }
    public Dictionary<string, object> Data { get; init; } = new();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record DataSourceInfo
{
    public required string DataSourceId { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public bool SupportsRealTime { get; init; }
}

#endregion
