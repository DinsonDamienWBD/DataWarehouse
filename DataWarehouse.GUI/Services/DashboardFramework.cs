using System.Collections.Concurrent;

namespace DataWarehouse.GUI.Services;

/// <summary>
/// Dashboard framework providing real-time dashboard data model,
/// widget management, and data source abstraction.
/// </summary>
public sealed class DashboardFramework
{
    private readonly ConcurrentDictionary<string, DashboardModel> _dashboards = new();
    private readonly ConcurrentDictionary<string, WidgetModel> _widgets = new();
    private readonly ConcurrentDictionary<string, DataSourceBinding> _dataBindings = new();
    private readonly ConcurrentDictionary<string, List<DashboardEvent>> _events = new();

    /// <summary>
    /// Creates a new dashboard.
    /// </summary>
    public DashboardModel CreateDashboard(string title, string? description = null, DashboardLayout layout = DashboardLayout.Grid)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var dashboard = new DashboardModel
        {
            DashboardId = id,
            Title = title,
            Description = description,
            Layout = layout,
            WidgetIds = new List<string>(),
            CreatedAt = DateTimeOffset.UtcNow,
            RefreshIntervalSeconds = 30
        };
        _dashboards[id] = dashboard;
        return dashboard;
    }

    /// <summary>
    /// Adds a widget to a dashboard.
    /// </summary>
    public WidgetModel AddWidget(string dashboardId, string title, WidgetType widgetType,
        int posX = 0, int posY = 0, int width = 4, int height = 3)
    {
        if (!_dashboards.TryGetValue(dashboardId, out var dashboard))
            throw new KeyNotFoundException($"Dashboard {dashboardId} not found");

        var widgetId = Guid.NewGuid().ToString("N")[..12];
        var widget = new WidgetModel
        {
            WidgetId = widgetId,
            DashboardId = dashboardId,
            Title = title,
            WidgetType = widgetType,
            PositionX = posX,
            PositionY = posY,
            Width = width,
            Height = height,
            Configuration = new Dictionary<string, object>(),
            CreatedAt = DateTimeOffset.UtcNow
        };
        _widgets[widgetId] = widget;

        dashboard.WidgetIds.Add(widgetId);
        _dashboards[dashboardId] = dashboard;

        return widget;
    }

    /// <summary>
    /// Binds a data source to a widget.
    /// </summary>
    public DataSourceBinding BindDataSource(string widgetId, string dataSourceType,
        string busTopicOrQuery, int refreshIntervalSeconds = 30)
    {
        var binding = new DataSourceBinding
        {
            BindingId = Guid.NewGuid().ToString("N")[..12],
            WidgetId = widgetId,
            DataSourceType = dataSourceType,
            BusTopicOrQuery = busTopicOrQuery,
            RefreshIntervalSeconds = refreshIntervalSeconds,
            CreatedAt = DateTimeOffset.UtcNow,
            IsActive = true
        };
        _dataBindings[binding.BindingId] = binding;
        return binding;
    }

    /// <summary>
    /// Gets a dashboard with all widgets.
    /// </summary>
    public DashboardView? GetDashboard(string dashboardId)
    {
        if (!_dashboards.TryGetValue(dashboardId, out var dashboard)) return null;

        var widgets = dashboard.WidgetIds
            .Select(wid => _widgets.TryGetValue(wid, out var w) ? w : null)
            .Where(w => w != null)
            .Select(w => w!)
            .ToList();

        return new DashboardView
        {
            Dashboard = dashboard,
            Widgets = widgets,
            DataBindings = _dataBindings.Values.Where(b => widgets.Any(w => w.WidgetId == b.WidgetId)).ToList()
        };
    }

    /// <summary>
    /// Lists all dashboards.
    /// </summary>
    public IReadOnlyList<DashboardModel> ListDashboards() =>
        _dashboards.Values.OrderByDescending(d => d.CreatedAt).ToList().AsReadOnly();

    /// <summary>
    /// Updates widget data (for real-time updates).
    /// </summary>
    public void UpdateWidgetData(string widgetId, Dictionary<string, object> data)
    {
        if (_widgets.TryGetValue(widgetId, out var widget))
        {
            _widgets[widgetId] = widget with
            {
                CurrentData = data,
                LastUpdatedAt = DateTimeOffset.UtcNow
            };

            RecordEvent(widget.DashboardId, new DashboardEvent
            {
                Type = "WidgetDataUpdate",
                WidgetId = widgetId,
                Timestamp = DateTimeOffset.UtcNow
            });
        }
    }

    /// <summary>
    /// Deletes a dashboard and its widgets.
    /// </summary>
    public bool DeleteDashboard(string dashboardId)
    {
        if (!_dashboards.TryRemove(dashboardId, out var dashboard)) return false;

        foreach (var widgetId in dashboard.WidgetIds)
        {
            _widgets.TryRemove(widgetId, out _);
            var bindings = _dataBindings.Values.Where(b => b.WidgetId == widgetId).ToList();
            foreach (var binding in bindings)
                _dataBindings.TryRemove(binding.BindingId, out _);
        }
        return true;
    }

    private void RecordEvent(string dashboardId, DashboardEvent evt)
    {
        _events.AddOrUpdate(
            dashboardId,
            _ => new List<DashboardEvent> { evt },
            (_, list) => { lock (list) { list.Add(evt); if (list.Count > 1000) list.RemoveAt(0); } return list; });
    }
}

/// <summary>
/// GUI connection manager for managing DataWarehouse instance connections.
/// </summary>
public sealed class ConnectionManager
{
    private readonly ConcurrentDictionary<string, ConnectionInfo> _connections = new();
    private string? _activeConnectionId;

    /// <summary>
    /// Adds a connection.
    /// </summary>
    public ConnectionInfo AddConnection(string name, string endpoint, string? authToken = null)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var conn = new ConnectionInfo
        {
            ConnectionId = id,
            Name = name,
            Endpoint = endpoint,
            AuthToken = authToken,
            Status = ConnectionStatus.Disconnected,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _connections[id] = conn;
        return conn;
    }

    /// <summary>
    /// Connects to a DataWarehouse instance.
    /// </summary>
    public async Task<ConnectionResult> ConnectAsync(string connectionId, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(connectionId, out var conn))
            return new ConnectionResult { Success = false, Error = "Connection not found" };

        _connections[connectionId] = conn with
        {
            Status = ConnectionStatus.Connecting,
            LastAttemptAt = DateTimeOffset.UtcNow
        };

        try
        {
            // Simulate connection attempt
            await Task.Delay(100, ct);

            _connections[connectionId] = conn with
            {
                Status = ConnectionStatus.Connected,
                ConnectedAt = DateTimeOffset.UtcNow,
                LastHealthCheckAt = DateTimeOffset.UtcNow
            };
            _activeConnectionId = connectionId;

            return new ConnectionResult { Success = true, ConnectionId = connectionId };
        }
        catch (Exception ex)
        {
            _connections[connectionId] = conn with
            {
                Status = ConnectionStatus.Error,
                LastError = ex.Message
            };
            return new ConnectionResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Disconnects from an instance.
    /// </summary>
    public bool Disconnect(string connectionId)
    {
        if (!_connections.TryGetValue(connectionId, out var conn)) return false;
        _connections[connectionId] = conn with { Status = ConnectionStatus.Disconnected };
        if (_activeConnectionId == connectionId) _activeConnectionId = null;
        return true;
    }

    /// <summary>
    /// Lists all connections.
    /// </summary>
    public IReadOnlyList<ConnectionInfo> ListConnections() =>
        _connections.Values.OrderBy(c => c.Name).ToList().AsReadOnly();

    /// <summary>
    /// Gets the active connection.
    /// </summary>
    public ConnectionInfo? GetActiveConnection() =>
        _activeConnectionId != null && _connections.TryGetValue(_activeConnectionId, out var conn) ? conn : null;

    /// <summary>
    /// Removes a saved connection.
    /// </summary>
    public bool RemoveConnection(string connectionId)
    {
        if (_activeConnectionId == connectionId) _activeConnectionId = null;
        return _connections.TryRemove(connectionId, out _);
    }
}

/// <summary>
/// Storage browser service for GUI object browsing.
/// </summary>
public sealed class StorageBrowser
{
    private readonly ConcurrentDictionary<string, StorageNode> _nodes = new();

    /// <summary>
    /// Lists objects in a storage path.
    /// </summary>
    public IReadOnlyList<StorageNode> ListObjects(string path = "/", int maxResults = 1000)
    {
        return _nodes.Values
            .Where(n => n.ParentPath == path)
            .OrderBy(n => n.IsDirectory ? 0 : 1)
            .ThenBy(n => n.Name)
            .Take(maxResults)
            .ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets object metadata.
    /// </summary>
    public StorageNode? GetObject(string path) =>
        _nodes.TryGetValue(path, out var node) ? node : null;

    /// <summary>
    /// Creates a directory.
    /// </summary>
    public StorageNode CreateDirectory(string path, string name)
    {
        var fullPath = $"{path.TrimEnd('/')}/{name}";
        var node = new StorageNode
        {
            Path = fullPath,
            Name = name,
            ParentPath = path,
            IsDirectory = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _nodes[fullPath] = node;
        return node;
    }

    /// <summary>
    /// Creates a file/object entry.
    /// </summary>
    public StorageNode CreateObject(string path, string name, long sizeBytes, string contentType)
    {
        var fullPath = $"{path.TrimEnd('/')}/{name}";
        var node = new StorageNode
        {
            Path = fullPath,
            Name = name,
            ParentPath = path,
            IsDirectory = false,
            SizeBytes = sizeBytes,
            ContentType = contentType,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _nodes[fullPath] = node;
        return node;
    }

    /// <summary>
    /// Deletes an object.
    /// </summary>
    public bool DeleteObject(string path) => _nodes.TryRemove(path, out _);

    /// <summary>
    /// Gets storage summary.
    /// </summary>
    public StorageSummary GetSummary()
    {
        var nodes = _nodes.Values.ToList();
        return new StorageSummary
        {
            TotalObjects = nodes.Count(n => !n.IsDirectory),
            TotalDirectories = nodes.Count(n => n.IsDirectory),
            TotalSizeBytes = nodes.Where(n => !n.IsDirectory).Sum(n => n.SizeBytes)
        };
    }
}

/// <summary>
/// Configuration editor service for GUI configuration management.
/// </summary>
public sealed class ConfigurationEditor
{
    private readonly ConcurrentDictionary<string, ConfigEntry> _entries = new();
    private readonly ConcurrentDictionary<string, List<ConfigChange>> _changeHistory = new();

    /// <summary>
    /// Gets a configuration value.
    /// </summary>
    public ConfigEntry? GetConfig(string key) =>
        _entries.TryGetValue(key, out var entry) ? entry : null;

    /// <summary>
    /// Sets a configuration value with validation.
    /// </summary>
    public ConfigSetResult SetConfig(string key, string value, string? changedBy = null)
    {
        var existing = _entries.TryGetValue(key, out var current) ? current : null;

        // Validate
        if (existing?.Validation != null)
        {
            var validationResult = ValidateValue(value, existing.Validation);
            if (!validationResult.IsValid)
                return new ConfigSetResult { Success = false, Error = validationResult.Error };
        }

        var entry = new ConfigEntry
        {
            Key = key,
            Value = value,
            PreviousValue = existing?.Value,
            DataType = existing?.DataType ?? "string",
            Description = existing?.Description,
            Validation = existing?.Validation,
            IsHotReloadable = existing?.IsHotReloadable ?? false,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = changedBy
        };
        _entries[key] = entry;

        // Record change
        var change = new ConfigChange
        {
            Key = key,
            OldValue = existing?.Value,
            NewValue = value,
            ChangedBy = changedBy ?? "system",
            ChangedAt = DateTimeOffset.UtcNow
        };
        _changeHistory.AddOrUpdate(
            key,
            _ => new List<ConfigChange> { change },
            (_, list) => { lock (list) { list.Add(change); } return list; });

        return new ConfigSetResult
        {
            Success = true,
            RequiresRestart = !(entry.IsHotReloadable),
            PreviousValue = existing?.Value
        };
    }

    /// <summary>
    /// Lists all configuration entries.
    /// </summary>
    public IReadOnlyList<ConfigEntry> ListConfigs(string? prefix = null) =>
        (prefix != null
            ? _entries.Values.Where(e => e.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            : _entries.Values)
        .OrderBy(e => e.Key).ToList().AsReadOnly();

    /// <summary>
    /// Gets change history for a key.
    /// </summary>
    public IReadOnlyList<ConfigChange> GetChangeHistory(string key) =>
        _changeHistory.TryGetValue(key, out var history) ? history.AsReadOnly() : Array.Empty<ConfigChange>();

    private static ValidationResult ValidateValue(string value, ConfigValidation validation)
    {
        if (validation.Required && string.IsNullOrWhiteSpace(value))
            return new ValidationResult { IsValid = false, Error = "Value is required" };

        if (validation.MinLength.HasValue && value.Length < validation.MinLength.Value)
            return new ValidationResult { IsValid = false, Error = $"Minimum length is {validation.MinLength}" };

        if (validation.MaxLength.HasValue && value.Length > validation.MaxLength.Value)
            return new ValidationResult { IsValid = false, Error = $"Maximum length is {validation.MaxLength}" };

        if (validation.Pattern != null)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, validation.Pattern))
                return new ValidationResult { IsValid = false, Error = $"Value does not match pattern: {validation.Pattern}" };
        }

        if (validation.AllowedValues != null && !validation.AllowedValues.Contains(value))
            return new ValidationResult { IsValid = false, Error = $"Value must be one of: {string.Join(", ", validation.AllowedValues)}" };

        return new ValidationResult { IsValid = true };
    }
}

#region Models

public sealed record DashboardModel
{
    public required string DashboardId { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public DashboardLayout Layout { get; init; }
    public List<string> WidgetIds { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public int RefreshIntervalSeconds { get; init; }
}

public enum DashboardLayout { Grid, FreeForm, Rows, Columns }

public sealed record WidgetModel
{
    public required string WidgetId { get; init; }
    public required string DashboardId { get; init; }
    public required string Title { get; init; }
    public WidgetType WidgetType { get; init; }
    public int PositionX { get; init; }
    public int PositionY { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public Dictionary<string, object> Configuration { get; init; } = new();
    public Dictionary<string, object>? CurrentData { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastUpdatedAt { get; init; }
}

public enum WidgetType { Chart, Table, Gauge, Log, Metric, Heatmap, Text, Alert }

public sealed record DataSourceBinding
{
    public required string BindingId { get; init; }
    public required string WidgetId { get; init; }
    public required string DataSourceType { get; init; }
    public required string BusTopicOrQuery { get; init; }
    public int RefreshIntervalSeconds { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public bool IsActive { get; init; }
}

public sealed record DashboardView
{
    public required DashboardModel Dashboard { get; init; }
    public List<WidgetModel> Widgets { get; init; } = new();
    public List<DataSourceBinding> DataBindings { get; init; } = new();
}

public sealed record DashboardEvent
{
    public required string Type { get; init; }
    public string? WidgetId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public sealed record ConnectionInfo
{
    public required string ConnectionId { get; init; }
    public required string Name { get; init; }
    public required string Endpoint { get; init; }
    public string? AuthToken { get; init; }
    public ConnectionStatus Status { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DateTimeOffset? LastHealthCheckAt { get; init; }
    public string? LastError { get; init; }
}

public enum ConnectionStatus { Disconnected, Connecting, Connected, Error }

public sealed record ConnectionResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? ConnectionId { get; init; }
}

public sealed record StorageNode
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string ParentPath { get; init; }
    public bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public string? ContentType { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
}

public sealed record StorageSummary
{
    public int TotalObjects { get; init; }
    public int TotalDirectories { get; init; }
    public long TotalSizeBytes { get; init; }
}

public sealed record ConfigEntry
{
    public required string Key { get; init; }
    public required string Value { get; init; }
    public string? PreviousValue { get; init; }
    public required string DataType { get; init; }
    public string? Description { get; init; }
    public ConfigValidation? Validation { get; init; }
    public bool IsHotReloadable { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }
}

public sealed record ConfigValidation
{
    public bool Required { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public string? Pattern { get; init; }
    public string[]? AllowedValues { get; init; }
}

public sealed record ConfigSetResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public bool RequiresRestart { get; init; }
    public string? PreviousValue { get; init; }
}

public sealed record ConfigChange
{
    public required string Key { get; init; }
    public string? OldValue { get; init; }
    public required string NewValue { get; init; }
    public required string ChangedBy { get; init; }
    public DateTimeOffset ChangedAt { get; init; }
}

public sealed record ValidationResult
{
    public bool IsValid { get; init; }
    public string? Error { get; init; }
}

#endregion
