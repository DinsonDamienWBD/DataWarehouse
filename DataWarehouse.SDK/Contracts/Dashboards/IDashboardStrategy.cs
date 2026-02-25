namespace DataWarehouse.SDK.Contracts.Dashboards;

/// <summary>
/// Defines the contract for dashboard strategy implementations providing dashboard creation, management, and visualization capabilities.
/// </summary>
/// <remarks>
/// <para>
/// Dashboard strategies enable:
/// <list type="bullet">
/// <item><description>Creating and configuring dashboards with multiple widgets</description></item>
/// <item><description>Real-time data visualization</description></item>
/// <item><description>Dashboard templating and reuse</description></item>
/// <item><description>Layout management and customization</description></item>
/// </list>
/// </para>
/// <para>
/// Implementations should support common visualization platforms (Grafana, Kibana, custom solutions).
/// </para>
/// </remarks>
public interface IDashboardStrategy : IDisposable
{
    /// <summary>
    /// Gets the capabilities supported by this dashboard strategy.
    /// </summary>
    /// <value>
    /// A <see cref="DashboardCapabilities"/> instance describing supported features.
    /// </value>
    DashboardCapabilities Capabilities { get; }

    /// <summary>
    /// Creates a new dashboard asynchronously.
    /// </summary>
    /// <param name="dashboard">The dashboard configuration to create.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that resolves to the created dashboard with assigned ID.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="dashboard"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the dashboard configuration is invalid or exceeds limits.</exception>
    /// <exception cref="NotSupportedException">Thrown when dashboard features are not supported.</exception>
    Task<Dashboard> CreateDashboardAsync(Dashboard dashboard, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing dashboard asynchronously.
    /// </summary>
    /// <param name="dashboard">The dashboard configuration to update (must have valid ID).</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that resolves to the updated dashboard.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="dashboard"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the dashboard ID is null or empty.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the dashboard does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the dashboard configuration is invalid.</exception>
    Task<Dashboard> UpdateDashboardAsync(Dashboard dashboard, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a dashboard by ID asynchronously.
    /// </summary>
    /// <param name="dashboardId">The unique identifier of the dashboard to retrieve.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that resolves to the requested dashboard.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="dashboardId"/> is null or empty.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the dashboard does not exist.</exception>
    Task<Dashboard> GetDashboardAsync(string dashboardId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a dashboard by ID asynchronously.
    /// </summary>
    /// <param name="dashboardId">The unique identifier of the dashboard to delete.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="dashboardId"/> is null or empty.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the dashboard does not exist.</exception>
    Task DeleteDashboardAsync(string dashboardId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all dashboards matching optional filter criteria.
    /// </summary>
    /// <param name="filter">Optional filter to apply (e.g., by tags, owner, time range).</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that resolves to a collection of dashboards matching the filter.</returns>
    Task<IReadOnlyList<Dashboard>> ListDashboardsAsync(DashboardFilter? filter = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a dashboard from a template asynchronously.
    /// </summary>
    /// <param name="templateId">The identifier of the template to use.</param>
    /// <param name="parameters">Parameters to customize the template.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that resolves to the created dashboard.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="templateId"/> is null or empty.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the template does not exist.</exception>
    /// <exception cref="NotSupportedException">Thrown when templates are not supported.</exception>
    Task<Dashboard> CreateFromTemplateAsync(string templateId, IReadOnlyDictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents filter criteria for listing dashboards.
/// </summary>
/// <param name="Tags">Filter by tags (dashboard must have all specified tags).</param>
/// <param name="Owner">Filter by owner/creator.</param>
/// <param name="CreatedAfter">Filter by creation date (after this date).</param>
/// <param name="CreatedBefore">Filter by creation date (before this date).</param>
/// <param name="SearchQuery">Full-text search query for dashboard title or description.</param>
public record DashboardFilter(
    IReadOnlyList<string>? Tags = null,
    string? Owner = null,
    DateTimeOffset? CreatedAfter = null,
    DateTimeOffset? CreatedBefore = null,
    string? SearchQuery = null)
{
    /// <summary>
    /// Creates an empty filter (returns all dashboards).
    /// </summary>
    public static DashboardFilter All() => new();
}
