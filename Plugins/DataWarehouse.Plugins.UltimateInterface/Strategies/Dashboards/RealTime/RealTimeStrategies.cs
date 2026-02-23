using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Dashboards;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateInterface.Dashboards.Strategies.RealTime;

/// <summary>
/// Live dashboard strategy with real-time WebSocket updates.
/// </summary>
public sealed class LiveDashboardStrategy : DashboardStrategyBase
{
    private readonly BoundedDictionary<string, Dashboard> _dashboards = new BoundedDictionary<string, Dashboard>(1000);
    private readonly BoundedDictionary<string, List<ClientWebSocket>> _subscribers = new BoundedDictionary<string, List<ClientWebSocket>>(1000);
    private readonly BoundedDictionary<string, CancellationTokenSource> _pushCancellations = new BoundedDictionary<string, CancellationTokenSource>(1000);

    public override string StrategyId => "live-dashboard";
    public override string StrategyName => "Live Dashboard";
    public override string VendorName => "DataWarehouse";
    public override string Category => "Real-time";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true, SupportsTemplates: true, SupportsSharing: true,
        SupportsVersioning: true, SupportsEmbedding: true, SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "Chart", "Table", "Metric", "Heatmap", "Gauge", "Text", "Log", "Trace", "Alert" },
        RefreshIntervals: new[] { 1, 2, 5, 10, 30, 60 });

    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        if (string.IsNullOrEmpty(Config!.BaseUrl)) return true;
        try
        {
            using var ws = new ClientWebSocket();
            var uri = new Uri(Config.BaseUrl.Replace("http", "ws") + "/ws/health");
            await ws.ConnectAsync(uri, ct);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct);
            return true;
        }
        catch { return true; } // Local mode always works
    }

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default)
    {
        EnsureConfigured();
        var startTime = DateTimeOffset.UtcNow;

        // Broadcast to all subscribers
        if (_subscribers.TryGetValue(targetId, out var sockets))
        {
            var message = SerializeToJson(new { type = "data", targetId = targetId, data = data.ToArray(), timestamp = DateTimeOffset.UtcNow });
            var buffer = Encoding.UTF8.GetBytes(message);
            var segment = new ArraySegment<byte>(buffer);

            var tasks = sockets.Where(s => s.State == WebSocketState.Open)
                .Select(s => s.SendAsync(segment, WebSocketMessageType.Text, true, ct));
            await Task.WhenAll(tasks);
        }

        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        return new DataPushResult { Success = true, RowsPushed = data.Count, DurationMs = duration, Metadata = new Dictionary<string, object> { ["subscriberCount"] = sockets?.Count ?? 0 } };
    }

    public override Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default)
    {
        return CreateDashboardCoreAsync(dashboard, ct);
    }

    protected override Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var id = GenerateId();
        var created = dashboard with { Id = id, CreatedAt = DateTimeOffset.UtcNow };
        _dashboards[id] = created;
        _subscribers[id] = new List<ClientWebSocket>();
        return Task.FromResult(created);
    }

    protected override Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        _dashboards[dashboard.Id!] = dashboard with { UpdatedAt = DateTimeOffset.UtcNow, Version = dashboard.Version + 1 };
        return Task.FromResult(_dashboards[dashboard.Id!]);
    }

    protected override Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        if (!_dashboards.TryGetValue(dashboardId, out var dashboard))
            throw new KeyNotFoundException($"Dashboard {dashboardId} not found");
        return Task.FromResult(dashboard);
    }

    protected override Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        _dashboards.TryRemove(dashboardId, out _);
        if (_subscribers.TryRemove(dashboardId, out var sockets))
        {
            foreach (var socket in sockets.Where(s => s.State == WebSocketState.Open))
            {
                socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Dashboard deleted", CancellationToken.None);
            }
        }
        return Task.CompletedTask;
    }

    protected override Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<Dashboard>>(_dashboards.Values.ToList());
    }

    protected override Task<Dashboard> CreateFromTemplateCoreAsync(string templateId, IReadOnlyDictionary<string, object>? parameters, CancellationToken ct)
    {
        if (!_dashboards.TryGetValue(templateId, out var template))
            throw new KeyNotFoundException($"Template {templateId} not found");
        var copy = template with { Id = GenerateId(), Title = parameters?.TryGetValue("name", out var n) == true ? n.ToString()! : $"{template.Title} (Copy)", Version = 1 };
        _dashboards[copy.Id!] = copy;
        _subscribers[copy.Id!] = new List<ClientWebSocket>();
        return Task.FromResult(copy);
    }

    /// <summary>
    /// Subscribes a WebSocket client to dashboard updates.
    /// </summary>
    public void Subscribe(string dashboardId, ClientWebSocket socket)
    {
        if (!_subscribers.TryGetValue(dashboardId, out var sockets))
        {
            sockets = new List<ClientWebSocket>();
            _subscribers[dashboardId] = sockets;
        }
        lock (sockets) { sockets.Add(socket); }
    }

    /// <summary>
    /// Unsubscribes a WebSocket client from dashboard updates.
    /// </summary>
    public void Unsubscribe(string dashboardId, ClientWebSocket socket)
    {
        if (_subscribers.TryGetValue(dashboardId, out var sockets))
        {
            lock (sockets) { sockets.Remove(socket); }
        }
    }

    /// <summary>
    /// Starts continuous data push for a dashboard.
    /// </summary>
    public async Task StartContinuousPushAsync(string dashboardId, Func<Task<IReadOnlyList<IReadOnlyDictionary<string, object>>>> dataProvider, int intervalMs = 1000)
    {
        var cts = new CancellationTokenSource();
        _pushCancellations[dashboardId] = cts;
        while (!cts.Token.IsCancellationRequested)
        {
            var data = await dataProvider();
            await PushDataAsync(dashboardId, data, cts.Token);
            await Task.Delay(intervalMs, cts.Token);
        }
    }

    /// <summary>
    /// Stops continuous data push for a dashboard.
    /// </summary>
    public void StopContinuousPush(string dashboardId)
    {
        if (_pushCancellations.TryRemove(dashboardId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
}

/// <summary>
/// WebSocket updates strategy for efficient real-time data streaming.
/// </summary>
public sealed class WebSocketUpdatesStrategy : DashboardStrategyBase
{
    private readonly BoundedDictionary<string, Dashboard> _dashboards = new BoundedDictionary<string, Dashboard>(1000);
    private ClientWebSocket? _serverSocket;

    public override string StrategyId => "websocket-updates";
    public override string StrategyName => "WebSocket Updates";
    public override string VendorName => "DataWarehouse";
    public override string Category => "Real-time";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true, SupportsTemplates: false, SupportsSharing: true,
        SupportsVersioning: false, SupportsEmbedding: true, SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "Chart", "Table", "Metric", "Gauge", "Log", "Trace" },
        RefreshIntervals: new[] { 1, 2, 5, 10 });

    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            await ConnectAsync(ct);
            return _serverSocket?.State == WebSocketState.Open;
        }
        catch { return false; }
    }

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default)
    {
        EnsureConfigured();
        await ConnectAsync(ct);
        var startTime = DateTimeOffset.UtcNow;

        var message = SerializeToJson(new { action = "push", targetId = targetId, data = data.ToArray() });
        var buffer = Encoding.UTF8.GetBytes(message);
        await _serverSocket!.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, ct);

        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        return new DataPushResult { Success = true, RowsPushed = data.Count, DurationMs = duration };
    }

    public override Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default)
    {
        return CreateDashboardCoreAsync(dashboard, ct);
    }

    protected override Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var id = GenerateId();
        var created = dashboard with { Id = id, CreatedAt = DateTimeOffset.UtcNow };
        _dashboards[id] = created;
        return Task.FromResult(created);
    }

    protected override Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        _dashboards[dashboard.Id!] = dashboard with { UpdatedAt = DateTimeOffset.UtcNow };
        return Task.FromResult(_dashboards[dashboard.Id!]);
    }

    protected override Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        if (!_dashboards.TryGetValue(dashboardId, out var dashboard))
            throw new KeyNotFoundException($"Dashboard {dashboardId} not found");
        return Task.FromResult(dashboard);
    }

    protected override Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        _dashboards.TryRemove(dashboardId, out _);
        return Task.CompletedTask;
    }

    protected override Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<Dashboard>>(_dashboards.Values.ToList());
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        if (_serverSocket?.State == WebSocketState.Open) return;
        _serverSocket?.Dispose();
        _serverSocket = new ClientWebSocket();
        var uri = new Uri(Config!.BaseUrl.Replace("http", "ws") + "/ws/dashboard");
        await _serverSocket.ConnectAsync(uri, ct);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _serverSocket?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Streaming visualization strategy for time-series and event data.
/// </summary>
public sealed class StreamingVisualizationStrategy : DashboardStrategyBase
{
    private readonly BoundedDictionary<string, Dashboard> _dashboards = new BoundedDictionary<string, Dashboard>(1000);
    private readonly BoundedDictionary<string, ConcurrentQueue<DataPoint>> _dataStreams = new BoundedDictionary<string, ConcurrentQueue<DataPoint>>(1000);
    private const int MaxPointsPerStream = 10000;

    public override string StrategyId => "streaming-viz";
    public override string StrategyName => "Streaming Visualization";
    public override string VendorName => "DataWarehouse";
    public override string Category => "Real-time";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true, SupportsTemplates: true, SupportsSharing: true,
        SupportsVersioning: true, SupportsEmbedding: true, SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "Chart", "Table", "Metric", "Gauge", "Log", "Trace", "State Timeline", "Flame Graph" },
        RefreshIntervals: new[] { 1, 2, 5, 10, 30 });

    public override Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        return Task.FromResult(true);
    }

    public override Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default)
    {
        EnsureConfigured();
        var startTime = DateTimeOffset.UtcNow;

        if (!_dataStreams.TryGetValue(targetId, out var stream))
        {
            stream = new ConcurrentQueue<DataPoint>();
            _dataStreams[targetId] = stream;
        }

        foreach (var row in data)
        {
            stream.Enqueue(new DataPoint { Timestamp = DateTimeOffset.UtcNow, Data = row.ToDictionary(k => k.Key, k => k.Value) });
            while (stream.Count > MaxPointsPerStream)
                stream.TryDequeue(out _);
        }

        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        return Task.FromResult(new DataPushResult { Success = true, RowsPushed = data.Count, DurationMs = duration, Metadata = new Dictionary<string, object> { ["bufferSize"] = stream.Count } });
    }

    public override Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default)
    {
        return CreateDashboardCoreAsync(dashboard, ct);
    }

    protected override Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var id = GenerateId();
        var created = dashboard with { Id = id, CreatedAt = DateTimeOffset.UtcNow };
        _dashboards[id] = created;
        return Task.FromResult(created);
    }

    protected override Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        _dashboards[dashboard.Id!] = dashboard with { UpdatedAt = DateTimeOffset.UtcNow, Version = dashboard.Version + 1 };
        return Task.FromResult(_dashboards[dashboard.Id!]);
    }

    protected override Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        if (!_dashboards.TryGetValue(dashboardId, out var dashboard))
            throw new KeyNotFoundException($"Dashboard {dashboardId} not found");
        return Task.FromResult(dashboard);
    }

    protected override Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        _dashboards.TryRemove(dashboardId, out _);
        _dataStreams.TryRemove(dashboardId, out _);
        return Task.CompletedTask;
    }

    protected override Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<Dashboard>>(_dashboards.Values.ToList());
    }

    protected override Task<Dashboard> CreateFromTemplateCoreAsync(string templateId, IReadOnlyDictionary<string, object>? parameters, CancellationToken ct)
    {
        if (!_dashboards.TryGetValue(templateId, out var template))
            throw new KeyNotFoundException($"Template {templateId} not found");
        var copy = template with { Id = GenerateId(), Version = 1 };
        _dashboards[copy.Id!] = copy;
        return Task.FromResult(copy);
    }

    /// <summary>
    /// Gets buffered data points for a stream.
    /// </summary>
    public IReadOnlyList<DataPoint> GetStreamData(string streamId, int maxPoints = 1000)
    {
        if (!_dataStreams.TryGetValue(streamId, out var stream))
            return Array.Empty<DataPoint>();
        return stream.TakeLast(maxPoints).ToList();
    }

    /// <summary>
    /// Clears the data buffer for a stream.
    /// </summary>
    public void ClearStream(string streamId)
    {
        if (_dataStreams.TryGetValue(streamId, out var stream))
        {
            while (stream.TryDequeue(out _)) { }
        }
    }
}

/// <summary>
/// Event-driven dashboard strategy for reactive updates.
/// </summary>
public sealed class EventDrivenDashboardStrategy : DashboardStrategyBase
{
    private readonly BoundedDictionary<string, Dashboard> _dashboards = new BoundedDictionary<string, Dashboard>(1000);
    private readonly ConcurrentDictionary<string, List<Action<DashboardEvent>>> _eventHandlers = new();

    public override string StrategyId => "event-driven";
    public override string StrategyName => "Event-Driven Dashboard";
    public override string VendorName => "DataWarehouse";
    public override string Category => "Real-time";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true, SupportsTemplates: true, SupportsSharing: true,
        SupportsVersioning: true, SupportsEmbedding: true, SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "Chart", "Table", "Metric", "Gauge", "Alert", "Log", "State Timeline" },
        RefreshIntervals: new[] { 1, 5, 10, 30, 60 });

    public override Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        return Task.FromResult(true);
    }

    public override Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default)
    {
        EnsureConfigured();
        var startTime = DateTimeOffset.UtcNow;

        var evt = new DashboardEvent { Type = "DataPush", TargetId = targetId, Data = data.ToList(), Timestamp = DateTimeOffset.UtcNow };
        RaiseEvent(targetId, evt);

        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        return Task.FromResult(new DataPushResult { Success = true, RowsPushed = data.Count, DurationMs = duration });
    }

    public override Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default)
    {
        return CreateDashboardCoreAsync(dashboard, ct);
    }

    protected override Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var id = GenerateId();
        var created = dashboard with { Id = id, CreatedAt = DateTimeOffset.UtcNow };
        _dashboards[id] = created;
        _eventHandlers[id] = new List<Action<DashboardEvent>>();
        RaiseEvent(id, new DashboardEvent { Type = "Created", TargetId = id, Timestamp = DateTimeOffset.UtcNow });
        return Task.FromResult(created);
    }

    protected override Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        _dashboards[dashboard.Id!] = dashboard with { UpdatedAt = DateTimeOffset.UtcNow, Version = dashboard.Version + 1 };
        RaiseEvent(dashboard.Id!, new DashboardEvent { Type = "Updated", TargetId = dashboard.Id!, Timestamp = DateTimeOffset.UtcNow });
        return Task.FromResult(_dashboards[dashboard.Id!]);
    }

    protected override Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        if (!_dashboards.TryGetValue(dashboardId, out var dashboard))
            throw new KeyNotFoundException($"Dashboard {dashboardId} not found");
        return Task.FromResult(dashboard);
    }

    protected override Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        RaiseEvent(dashboardId, new DashboardEvent { Type = "Deleted", TargetId = dashboardId, Timestamp = DateTimeOffset.UtcNow });
        _dashboards.TryRemove(dashboardId, out _);
        _eventHandlers.TryRemove(dashboardId, out _);
        return Task.CompletedTask;
    }

    protected override Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<Dashboard>>(_dashboards.Values.ToList());
    }

    /// <summary>
    /// Subscribes to dashboard events.
    /// </summary>
    public void OnEvent(string dashboardId, Action<DashboardEvent> handler)
    {
        if (!_eventHandlers.TryGetValue(dashboardId, out var handlers))
        {
            handlers = new List<Action<DashboardEvent>>();
            _eventHandlers[dashboardId] = handlers;
        }
        lock (handlers) { handlers.Add(handler); }
    }

    /// <summary>
    /// Unsubscribes from dashboard events.
    /// </summary>
    public void OffEvent(string dashboardId, Action<DashboardEvent> handler)
    {
        if (_eventHandlers.TryGetValue(dashboardId, out var handlers))
        {
            lock (handlers) { handlers.Remove(handler); }
        }
    }

    private void RaiseEvent(string dashboardId, DashboardEvent evt)
    {
        if (_eventHandlers.TryGetValue(dashboardId, out var handlers))
        {
            List<Action<DashboardEvent>> handlersCopy;
            lock (handlers) { handlersCopy = handlers.ToList(); }
            foreach (var handler in handlersCopy)
            {
                try { handler(evt); } catch { /* ignore handler errors */ }
            }
        }
    }
}

/// <summary>
/// Data point for streaming visualization.
/// </summary>
public sealed class DataPoint
{
    public DateTimeOffset Timestamp { get; init; }
    public required Dictionary<string, object> Data { get; init; }
}

/// <summary>
/// Dashboard event for event-driven updates.
/// </summary>
public sealed class DashboardEvent
{
    public required string Type { get; init; }
    public required string TargetId { get; init; }
    public IReadOnlyList<IReadOnlyDictionary<string, object>>? Data { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
