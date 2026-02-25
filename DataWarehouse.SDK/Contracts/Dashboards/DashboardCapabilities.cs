namespace DataWarehouse.SDK.Contracts.Dashboards;

/// <summary>
/// Describes the dashboard capabilities supported by a dashboard strategy implementation.
/// </summary>
/// <param name="SupportsRealTime">Whether the strategy supports real-time data updates in dashboards.</param>
/// <param name="SupportsTemplates">Whether the strategy supports dashboard templates for reusability.</param>
/// <param name="SupportsSharing">Whether the strategy supports sharing dashboards with other users.</param>
/// <param name="SupportsVersioning">Whether the strategy supports versioning and rollback of dashboard configurations.</param>
/// <param name="SupportsEmbedding">Whether dashboards can be embedded in external applications.</param>
/// <param name="SupportsAlerting">Whether dashboards can trigger alerts based on widget data.</param>
/// <param name="SupportedWidgetTypes">List of supported widget types (e.g., "Chart", "Table", "Metric", "Heatmap").</param>
/// <param name="MaxWidgetsPerDashboard">Maximum number of widgets allowed per dashboard (null if unlimited).</param>
/// <param name="MaxDashboards">Maximum number of dashboards allowed (null if unlimited).</param>
/// <param name="RefreshIntervals">Supported refresh intervals in seconds (e.g., [5, 10, 30, 60, 300]).</param>
/// <remarks>
/// <para>
/// This record provides discovery capabilities for consumers to determine what dashboard
/// features are available before attempting to use them.
/// </para>
/// <para>
/// Common widget types include:
/// <list type="bullet">
/// <item><term>Chart</term><description>Line, bar, pie, area charts for time-series or categorical data</description></item>
/// <item><term>Table</term><description>Tabular data display with sorting and filtering</description></item>
/// <item><term>Metric</term><description>Single numeric value with optional sparkline</description></item>
/// <item><term>Heatmap</term><description>Color-coded matrix visualization</description></item>
/// <item><term>Gauge</term><description>Circular or linear gauge for percentage or threshold values</description></item>
/// <item><term>Text</term><description>Static or dynamic text panels</description></item>
/// <item><term>Log</term><description>Log stream viewer</description></item>
/// <item><term>Trace</term><description>Distributed trace visualization</description></item>
/// </list>
/// </para>
/// </remarks>
public record DashboardCapabilities(
    bool SupportsRealTime,
    bool SupportsTemplates,
    bool SupportsSharing,
    bool SupportsVersioning,
    bool SupportsEmbedding,
    bool SupportsAlerting,
    IReadOnlyList<string> SupportedWidgetTypes,
    int? MaxWidgetsPerDashboard = null,
    int? MaxDashboards = null,
    IReadOnlyList<int>? RefreshIntervals = null)
{
    /// <summary>
    /// Gets a value indicating whether this strategy has any dashboard capabilities.
    /// </summary>
    public bool HasAnyCapability => SupportedWidgetTypes.Count > 0;

    /// <summary>
    /// Gets a value indicating whether this strategy supports advanced dashboard features.
    /// </summary>
    public bool HasAdvancedFeatures => SupportsTemplates || SupportsVersioning || SupportsEmbedding;

    /// <summary>
    /// Creates a minimal dashboard capabilities instance (basic features only).
    /// </summary>
    /// <param name="widgetTypes">Supported widget types.</param>
    /// <returns>A capabilities instance with basic features enabled.</returns>
    public static DashboardCapabilities Minimal(params string[] widgetTypes) => new(
        SupportsRealTime: false,
        SupportsTemplates: false,
        SupportsSharing: false,
        SupportsVersioning: false,
        SupportsEmbedding: false,
        SupportsAlerting: false,
        SupportedWidgetTypes: widgetTypes.Length > 0 ? widgetTypes : new[] { "Chart", "Table", "Metric" },
        MaxWidgetsPerDashboard: 10,
        MaxDashboards: 100,
        RefreshIntervals: null);

    /// <summary>
    /// Creates a full-featured dashboard capabilities instance.
    /// </summary>
    /// <param name="widgetTypes">Supported widget types (uses common types if empty).</param>
    /// <returns>A capabilities instance with all features enabled.</returns>
    public static DashboardCapabilities Full(params string[] widgetTypes) => new(
        SupportsRealTime: true,
        SupportsTemplates: true,
        SupportsSharing: true,
        SupportsVersioning: true,
        SupportsEmbedding: true,
        SupportsAlerting: true,
        SupportedWidgetTypes: widgetTypes.Length > 0 ? widgetTypes : new[]
        {
            "Chart", "Table", "Metric", "Heatmap", "Gauge", "Text", "Log", "Trace"
        },
        MaxWidgetsPerDashboard: null,
        MaxDashboards: null,
        RefreshIntervals: new[] { 5, 10, 30, 60, 300, 900, 1800, 3600 });

    /// <summary>
    /// Checks if a specific widget type is supported.
    /// </summary>
    /// <param name="widgetType">The widget type to check (case-insensitive).</param>
    /// <returns>True if the widget type is supported; otherwise, false.</returns>
    public bool SupportsWidgetType(string widgetType) =>
        SupportedWidgetTypes.Any(w => w.Equals(widgetType, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Checks if a specific refresh interval is supported.
    /// </summary>
    /// <param name="intervalSeconds">The refresh interval in seconds.</param>
    /// <returns>True if the interval is supported; false if not or if no specific intervals are defined.</returns>
    public bool SupportsRefreshInterval(int intervalSeconds) =>
        RefreshIntervals?.Contains(intervalSeconds) ?? false;

    /// <summary>
    /// Validates if a dashboard configuration is within the strategy's limits.
    /// </summary>
    /// <param name="widgetCount">Number of widgets in the dashboard.</param>
    /// <returns>True if the widget count is within limits; otherwise, false.</returns>
    public bool IsWithinWidgetLimit(int widgetCount) =>
        MaxWidgetsPerDashboard == null || widgetCount <= MaxWidgetsPerDashboard.Value;
}
