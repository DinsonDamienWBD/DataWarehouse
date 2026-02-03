namespace DataWarehouse.SDK.Contracts.Dashboards;

/// <summary>
/// Represents a complete dashboard configuration including metadata, layout, and widgets.
/// </summary>
/// <param name="Id">Unique identifier for the dashboard (null for new dashboards).</param>
/// <param name="Title">Human-readable title of the dashboard.</param>
/// <param name="Description">Optional description explaining the dashboard's purpose.</param>
/// <param name="Layout">Layout configuration determining widget positioning.</param>
/// <param name="Widgets">Collection of widgets displayed on the dashboard.</param>
/// <param name="TimeRange">Default time range for time-series data in widgets.</param>
/// <param name="RefreshInterval">Auto-refresh interval in seconds (null to disable auto-refresh).</param>
/// <param name="Tags">Tags for categorization and filtering.</param>
/// <param name="Owner">Identifier of the dashboard owner/creator.</param>
/// <param name="CreatedAt">When the dashboard was created.</param>
/// <param name="UpdatedAt">When the dashboard was last updated.</param>
/// <param name="Version">Version number for versioned dashboards.</param>
/// <remarks>
/// <para>
/// Dashboards aggregate multiple widgets into a unified view for monitoring and analysis.
/// Each dashboard can have a custom layout, time range, and refresh behavior.
/// </para>
/// </remarks>
public record Dashboard(
    string? Id,
    string Title,
    string? Description,
    DashboardLayout Layout,
    IReadOnlyList<DashboardWidget> Widgets,
    TimeRange TimeRange,
    int? RefreshInterval = null,
    IReadOnlyList<string>? Tags = null,
    string? Owner = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    int Version = 1)
{
    /// <summary>
    /// Creates a new dashboard with default settings.
    /// </summary>
    /// <param name="title">Dashboard title.</param>
    /// <param name="description">Optional description.</param>
    /// <returns>A new dashboard instance.</returns>
    public static Dashboard Create(string title, string? description = null) => new(
        Id: null,
        Title: title,
        Description: description,
        Layout: DashboardLayout.Grid(columns: 12),
        Widgets: Array.Empty<DashboardWidget>(),
        TimeRange: TimeRange.Last24Hours(),
        RefreshInterval: null,
        Tags: null,
        Owner: null,
        CreatedAt: null,
        UpdatedAt: null,
        Version: 1);

    /// <summary>
    /// Creates a copy of this dashboard with updated widgets.
    /// </summary>
    /// <param name="widgets">New widget collection.</param>
    /// <returns>A new dashboard instance with updated widgets.</returns>
    public Dashboard WithWidgets(IReadOnlyList<DashboardWidget> widgets) =>
        this with { Widgets = widgets, UpdatedAt = DateTimeOffset.UtcNow };

    /// <summary>
    /// Adds a widget to this dashboard.
    /// </summary>
    /// <param name="widget">Widget to add.</param>
    /// <returns>A new dashboard instance with the added widget.</returns>
    public Dashboard AddWidget(DashboardWidget widget)
    {
        var newWidgets = Widgets.ToList();
        newWidgets.Add(widget);
        return WithWidgets(newWidgets);
    }
}

/// <summary>
/// Represents a single widget within a dashboard.
/// </summary>
/// <param name="Id">Unique identifier for the widget within the dashboard.</param>
/// <param name="Title">Widget title displayed in the dashboard.</param>
/// <param name="Type">Type of widget (determines visualization and behavior).</param>
/// <param name="Position">Position and size of the widget in the layout.</param>
/// <param name="DataSource">Data source configuration for the widget.</param>
/// <param name="Configuration">Widget-specific configuration options.</param>
/// <remarks>
/// Widgets are the building blocks of dashboards. Each widget has a specific type,
/// data source, and position within the dashboard layout.
/// </remarks>
public record DashboardWidget(
    string Id,
    string Title,
    WidgetType Type,
    WidgetPosition Position,
    DataSourceConfiguration DataSource,
    IReadOnlyDictionary<string, object>? Configuration = null)
{
    /// <summary>
    /// Creates a chart widget.
    /// </summary>
    /// <param name="title">Widget title.</param>
    /// <param name="dataSource">Data source configuration.</param>
    /// <param name="position">Widget position.</param>
    /// <returns>A new chart widget instance.</returns>
    public static DashboardWidget Chart(string title, DataSourceConfiguration dataSource, WidgetPosition? position = null) => new(
        Id: Guid.NewGuid().ToString(),
        Title: title,
        Type: WidgetType.Chart,
        Position: position ?? WidgetPosition.Auto(),
        DataSource: dataSource);

    /// <summary>
    /// Creates a metric widget (single value display).
    /// </summary>
    /// <param name="title">Widget title.</param>
    /// <param name="dataSource">Data source configuration.</param>
    /// <param name="position">Widget position.</param>
    /// <returns>A new metric widget instance.</returns>
    public static DashboardWidget Metric(string title, DataSourceConfiguration dataSource, WidgetPosition? position = null) => new(
        Id: Guid.NewGuid().ToString(),
        Title: title,
        Type: WidgetType.Metric,
        Position: position ?? WidgetPosition.Auto(),
        DataSource: dataSource);

    /// <summary>
    /// Creates a table widget.
    /// </summary>
    /// <param name="title">Widget title.</param>
    /// <param name="dataSource">Data source configuration.</param>
    /// <param name="position">Widget position.</param>
    /// <returns>A new table widget instance.</returns>
    public static DashboardWidget Table(string title, DataSourceConfiguration dataSource, WidgetPosition? position = null) => new(
        Id: Guid.NewGuid().ToString(),
        Title: title,
        Type: WidgetType.Table,
        Position: position ?? WidgetPosition.Auto(),
        DataSource: dataSource);
}

/// <summary>
/// Defines the type of dashboard widget.
/// </summary>
public enum WidgetType
{
    /// <summary>Chart visualization (line, bar, pie, area, etc.).</summary>
    Chart = 0,

    /// <summary>Tabular data display.</summary>
    Table = 1,

    /// <summary>Single metric value display.</summary>
    Metric = 2,

    /// <summary>Color-coded matrix visualization.</summary>
    Heatmap = 3,

    /// <summary>Gauge display (circular or linear).</summary>
    Gauge = 4,

    /// <summary>Text panel (static or dynamic).</summary>
    Text = 5,

    /// <summary>Log stream viewer.</summary>
    Log = 6,

    /// <summary>Distributed trace visualization.</summary>
    Trace = 7,

    /// <summary>Alert status display.</summary>
    Alert = 8,

    /// <summary>Custom widget type (implementation-specific).</summary>
    Custom = 99
}

/// <summary>
/// Represents the layout configuration for a dashboard.
/// </summary>
/// <param name="Type">Type of layout (Grid, Freeform, etc.).</param>
/// <param name="Columns">Number of columns in grid layouts.</param>
/// <param name="RowHeight">Height of each row in pixels (for grid layouts).</param>
/// <param name="Properties">Additional layout-specific properties.</param>
public record DashboardLayout(
    LayoutType Type,
    int Columns = 12,
    int RowHeight = 100,
    IReadOnlyDictionary<string, object>? Properties = null)
{
    /// <summary>
    /// Creates a grid-based layout.
    /// </summary>
    /// <param name="columns">Number of columns in the grid.</param>
    /// <param name="rowHeight">Height of each row in pixels.</param>
    /// <returns>A grid layout configuration.</returns>
    public static DashboardLayout Grid(int columns = 12, int rowHeight = 100) => new(
        Type: LayoutType.Grid,
        Columns: columns,
        RowHeight: rowHeight);

    /// <summary>
    /// Creates a freeform layout (absolute positioning).
    /// </summary>
    /// <returns>A freeform layout configuration.</returns>
    public static DashboardLayout Freeform() => new(
        Type: LayoutType.Freeform,
        Columns: 0,
        RowHeight: 0);
}

/// <summary>
/// Defines the layout type for dashboards.
/// </summary>
public enum LayoutType
{
    /// <summary>Grid-based layout with responsive positioning.</summary>
    Grid = 0,

    /// <summary>Freeform layout with absolute positioning.</summary>
    Freeform = 1
}

/// <summary>
/// Represents the position and size of a widget within a dashboard layout.
/// </summary>
/// <param name="X">Horizontal position (column index in grid layouts, pixels in freeform).</param>
/// <param name="Y">Vertical position (row index in grid layouts, pixels in freeform).</param>
/// <param name="Width">Widget width (column span in grid layouts, pixels in freeform).</param>
/// <param name="Height">Widget height (row span in grid layouts, pixels in freeform).</param>
public record WidgetPosition(int X, int Y, int Width, int Height)
{
    /// <summary>
    /// Creates an auto-positioned widget (lets the dashboard decide placement).
    /// </summary>
    /// <returns>A default widget position.</returns>
    public static WidgetPosition Auto() => new(X: 0, Y: 0, Width: 6, Height: 3);

    /// <summary>
    /// Creates a full-width widget position.
    /// </summary>
    /// <param name="height">Height in rows.</param>
    /// <returns>A full-width widget position.</returns>
    public static WidgetPosition FullWidth(int height = 3) => new(X: 0, Y: 0, Width: 12, Height: height);
}

/// <summary>
/// Represents a time range for dashboard data queries.
/// </summary>
/// <param name="From">Start of the time range (null for relative ranges).</param>
/// <param name="To">End of the time range (null for relative ranges or "now").</param>
/// <param name="RelativeRange">Relative time range description (e.g., "last 24 hours", "last 7 days").</param>
public record TimeRange(DateTimeOffset? From, DateTimeOffset? To, string? RelativeRange = null)
{
    /// <summary>
    /// Creates a time range for the last N hours.
    /// </summary>
    /// <param name="hours">Number of hours.</param>
    /// <returns>A relative time range.</returns>
    public static TimeRange LastHours(int hours) => new(
        From: null,
        To: null,
        RelativeRange: $"last {hours} hours");

    /// <summary>
    /// Creates a time range for the last 24 hours.
    /// </summary>
    /// <returns>A relative time range for the last 24 hours.</returns>
    public static TimeRange Last24Hours() => LastHours(24);

    /// <summary>
    /// Creates a time range for the last N days.
    /// </summary>
    /// <param name="days">Number of days.</param>
    /// <returns>A relative time range.</returns>
    public static TimeRange LastDays(int days) => new(
        From: null,
        To: null,
        RelativeRange: $"last {days} days");

    /// <summary>
    /// Creates an absolute time range.
    /// </summary>
    /// <param name="from">Start time.</param>
    /// <param name="to">End time.</param>
    /// <returns>An absolute time range.</returns>
    public static TimeRange Absolute(DateTimeOffset from, DateTimeOffset to) => new(
        From: from,
        To: to,
        RelativeRange: null);

    /// <summary>
    /// Gets a value indicating whether this is a relative time range.
    /// </summary>
    public bool IsRelative => RelativeRange != null;
}

/// <summary>
/// Represents data source configuration for a widget.
/// </summary>
/// <param name="Type">Type of data source (Metrics, Logs, Traces, etc.).</param>
/// <param name="Query">Query string or expression to retrieve data.</param>
/// <param name="Parameters">Additional parameters for the data source.</param>
/// <remarks>
/// Data sources define where and how widgets retrieve their data.
/// Query syntax is implementation-specific and depends on the data source type.
/// </remarks>
public record DataSourceConfiguration(
    string Type,
    string Query,
    IReadOnlyDictionary<string, object>? Parameters = null)
{
    /// <summary>
    /// Creates a metrics data source configuration.
    /// </summary>
    /// <param name="query">Metrics query (e.g., PromQL, SQL).</param>
    /// <returns>A metrics data source configuration.</returns>
    public static DataSourceConfiguration Metrics(string query) => new(
        Type: "Metrics",
        Query: query);

    /// <summary>
    /// Creates a logs data source configuration.
    /// </summary>
    /// <param name="query">Logs query (e.g., LogQL, Lucene).</param>
    /// <returns>A logs data source configuration.</returns>
    public static DataSourceConfiguration Logs(string query) => new(
        Type: "Logs",
        Query: query);

    /// <summary>
    /// Creates a traces data source configuration.
    /// </summary>
    /// <param name="query">Traces query.</param>
    /// <returns>A traces data source configuration.</returns>
    public static DataSourceConfiguration Traces(string query) => new(
        Type: "Traces",
        Query: query);
}
