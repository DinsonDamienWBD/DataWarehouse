using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.Perses
{
    #region Configuration

    /// <summary>
    /// Configuration options for the Perses plugin.
    /// </summary>
    public sealed class PersesConfiguration
    {
        /// <summary>
        /// Gets or sets the base URL for the Perses API.
        /// </summary>
        /// <remarks>WARNING: Default value is for development only. Configure for production.</remarks>
        public string PersesUrl { get; set; } = "http://localhost:8080";

        /// <summary>
        /// Gets or sets the API key for authentication (optional).
        /// </summary>
        public string? ApiKey { get; set; }

        /// <summary>
        /// Gets or sets the project name in Perses.
        /// </summary>
        public string Project { get; set; } = "default";

        /// <summary>
        /// Gets or sets the timeout for HTTP requests.
        /// </summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets whether to create missing dashboards automatically.
        /// </summary>
        public bool AutoCreateDashboards { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to create missing datasources automatically.
        /// </summary>
        public bool AutoCreateDatasources { get; set; } = true;

        /// <summary>
        /// Gets or sets the default datasource name.
        /// </summary>
        public string DefaultDatasource { get; set; } = "prometheus";

        /// <summary>
        /// Gets or sets whether to validate SSL certificates.
        /// </summary>
        public bool ValidateSsl { get; set; } = true;

        /// <summary>
        /// Gets or sets the refresh interval for dashboards.
        /// </summary>
        public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);
    }

    #endregion

    #region API Models

    /// <summary>
    /// Represents a Perses dashboard.
    /// </summary>
    public sealed class PersesDashboard
    {
        /// <summary>
        /// Gets or sets the dashboard metadata.
        /// </summary>
        [JsonPropertyName("metadata")]
        public DashboardMetadata Metadata { get; set; } = new();

        /// <summary>
        /// Gets or sets the dashboard specification.
        /// </summary>
        [JsonPropertyName("spec")]
        public DashboardSpec Spec { get; set; } = new();
    }

    /// <summary>
    /// Metadata for a dashboard.
    /// </summary>
    public sealed class DashboardMetadata
    {
        /// <summary>
        /// Gets or sets the dashboard name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the project name.
        /// </summary>
        [JsonPropertyName("project")]
        public string Project { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the creation timestamp.
        /// </summary>
        [JsonPropertyName("createdAt")]
        public DateTime? CreatedAt { get; set; }

        /// <summary>
        /// Gets or sets the last update timestamp.
        /// </summary>
        [JsonPropertyName("updatedAt")]
        public DateTime? UpdatedAt { get; set; }

        /// <summary>
        /// Gets or sets the dashboard version.
        /// </summary>
        [JsonPropertyName("version")]
        public int Version { get; set; }
    }

    /// <summary>
    /// Specification for a dashboard.
    /// </summary>
    public sealed class DashboardSpec
    {
        /// <summary>
        /// Gets or sets the dashboard display name.
        /// </summary>
        [JsonPropertyName("display")]
        public DashboardDisplay Display { get; set; } = new();

        /// <summary>
        /// Gets or sets the dashboard variables.
        /// </summary>
        [JsonPropertyName("variables")]
        public List<DashboardVariable> Variables { get; set; } = new();

        /// <summary>
        /// Gets or sets the dashboard panels.
        /// </summary>
        [JsonPropertyName("panels")]
        public List<DashboardPanel> Panels { get; set; } = new();

        /// <summary>
        /// Gets or sets the dashboard layouts.
        /// </summary>
        [JsonPropertyName("layouts")]
        public List<DashboardLayout> Layouts { get; set; } = new();

        /// <summary>
        /// Gets or sets the dashboard datasources.
        /// </summary>
        [JsonPropertyName("datasources")]
        public Dictionary<string, DatasourceReference> Datasources { get; set; } = new();

        /// <summary>
        /// Gets or sets the refresh interval in seconds.
        /// </summary>
        [JsonPropertyName("refresh_interval")]
        public int? RefreshInterval { get; set; }
    }

    /// <summary>
    /// Display settings for a dashboard.
    /// </summary>
    public sealed class DashboardDisplay
    {
        /// <summary>
        /// Gets or sets the dashboard title.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the dashboard description.
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }

    /// <summary>
    /// Represents a dashboard variable.
    /// </summary>
    public sealed class DashboardVariable
    {
        /// <summary>
        /// Gets or sets the variable name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the variable kind.
        /// </summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "query";

        /// <summary>
        /// Gets or sets the variable spec.
        /// </summary>
        [JsonPropertyName("spec")]
        public Dictionary<string, object> Spec { get; set; } = new();
    }

    /// <summary>
    /// Represents a dashboard panel.
    /// </summary>
    public sealed class DashboardPanel
    {
        /// <summary>
        /// Gets or sets the panel name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the panel kind.
        /// </summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "panel";

        /// <summary>
        /// Gets or sets the panel spec.
        /// </summary>
        [JsonPropertyName("spec")]
        public PanelSpec Spec { get; set; } = new();
    }

    /// <summary>
    /// Specification for a panel.
    /// </summary>
    public sealed class PanelSpec
    {
        /// <summary>
        /// Gets or sets the panel display settings.
        /// </summary>
        [JsonPropertyName("display")]
        public PanelDisplay Display { get; set; } = new();

        /// <summary>
        /// Gets or sets the panel queries.
        /// </summary>
        [JsonPropertyName("queries")]
        public List<PanelQuery> Queries { get; set; } = new();

        /// <summary>
        /// Gets or sets the panel plugin.
        /// </summary>
        [JsonPropertyName("plugin")]
        public PanelPlugin Plugin { get; set; } = new();
    }

    /// <summary>
    /// Display settings for a panel.
    /// </summary>
    public sealed class PanelDisplay
    {
        /// <summary>
        /// Gets or sets the panel title.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the panel description.
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }

    /// <summary>
    /// Represents a panel query.
    /// </summary>
    public sealed class PanelQuery
    {
        /// <summary>
        /// Gets or sets the query kind.
        /// </summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "PromQL";

        /// <summary>
        /// Gets or sets the query spec.
        /// </summary>
        [JsonPropertyName("spec")]
        public QuerySpec Spec { get; set; } = new();
    }

    /// <summary>
    /// Specification for a query.
    /// </summary>
    public sealed class QuerySpec
    {
        /// <summary>
        /// Gets or sets the query expression.
        /// </summary>
        [JsonPropertyName("query")]
        public string Query { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the datasource reference.
        /// </summary>
        [JsonPropertyName("datasource")]
        public DatasourceReference? Datasource { get; set; }
    }

    /// <summary>
    /// Represents a panel plugin.
    /// </summary>
    public sealed class PanelPlugin
    {
        /// <summary>
        /// Gets or sets the plugin kind.
        /// </summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "TimeSeriesChart";

        /// <summary>
        /// Gets or sets the plugin spec.
        /// </summary>
        [JsonPropertyName("spec")]
        public Dictionary<string, object>? Spec { get; set; }
    }

    /// <summary>
    /// Represents a dashboard layout.
    /// </summary>
    public sealed class DashboardLayout
    {
        /// <summary>
        /// Gets or sets the layout kind.
        /// </summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "Grid";

        /// <summary>
        /// Gets or sets the layout spec.
        /// </summary>
        [JsonPropertyName("spec")]
        public LayoutSpec Spec { get; set; } = new();
    }

    /// <summary>
    /// Specification for a layout.
    /// </summary>
    public sealed class LayoutSpec
    {
        /// <summary>
        /// Gets or sets the layout items.
        /// </summary>
        [JsonPropertyName("items")]
        public List<LayoutItem> Items { get; set; } = new();
    }

    /// <summary>
    /// Represents a layout item.
    /// </summary>
    public sealed class LayoutItem
    {
        /// <summary>
        /// Gets or sets the panel reference.
        /// </summary>
        [JsonPropertyName("panel")]
        public string Panel { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the X position.
        /// </summary>
        [JsonPropertyName("x")]
        public int X { get; set; }

        /// <summary>
        /// Gets or sets the Y position.
        /// </summary>
        [JsonPropertyName("y")]
        public int Y { get; set; }

        /// <summary>
        /// Gets or sets the width.
        /// </summary>
        [JsonPropertyName("width")]
        public int Width { get; set; } = 12;

        /// <summary>
        /// Gets or sets the height.
        /// </summary>
        [JsonPropertyName("height")]
        public int Height { get; set; } = 6;
    }

    /// <summary>
    /// Represents a datasource.
    /// </summary>
    public sealed class PersesDatasource
    {
        /// <summary>
        /// Gets or sets the datasource metadata.
        /// </summary>
        [JsonPropertyName("metadata")]
        public DatasourceMetadata Metadata { get; set; } = new();

        /// <summary>
        /// Gets or sets the datasource specification.
        /// </summary>
        [JsonPropertyName("spec")]
        public DatasourceSpec Spec { get; set; } = new();
    }

    /// <summary>
    /// Metadata for a datasource.
    /// </summary>
    public sealed class DatasourceMetadata
    {
        /// <summary>
        /// Gets or sets the datasource name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the project name.
        /// </summary>
        [JsonPropertyName("project")]
        public string Project { get; set; } = string.Empty;
    }

    /// <summary>
    /// Specification for a datasource.
    /// </summary>
    public sealed class DatasourceSpec
    {
        /// <summary>
        /// Gets or sets the datasource plugin.
        /// </summary>
        [JsonPropertyName("plugin")]
        public DatasourcePlugin Plugin { get; set; } = new();

        /// <summary>
        /// Gets or sets whether this is the default datasource.
        /// </summary>
        [JsonPropertyName("default")]
        public bool IsDefault { get; set; }
    }

    /// <summary>
    /// Represents a datasource plugin.
    /// </summary>
    public sealed class DatasourcePlugin
    {
        /// <summary>
        /// Gets or sets the plugin kind.
        /// </summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "PrometheusDataSource";

        /// <summary>
        /// Gets or sets the plugin spec.
        /// </summary>
        [JsonPropertyName("spec")]
        public DatasourcePluginSpec Spec { get; set; } = new();
    }

    /// <summary>
    /// Specification for a datasource plugin.
    /// </summary>
    public sealed class DatasourcePluginSpec
    {
        /// <summary>
        /// Gets or sets the datasource URL.
        /// </summary>
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the HTTP proxy configuration.
        /// </summary>
        [JsonPropertyName("proxy")]
        public ProxyConfig? Proxy { get; set; }
    }

    /// <summary>
    /// Represents a datasource reference.
    /// </summary>
    public sealed class DatasourceReference
    {
        /// <summary>
        /// Gets or sets the datasource kind.
        /// </summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "PrometheusDataSource";

        /// <summary>
        /// Gets or sets the datasource name.
        /// </summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    /// <summary>
    /// Represents proxy configuration.
    /// </summary>
    public sealed class ProxyConfig
    {
        /// <summary>
        /// Gets or sets the proxy URL.
        /// </summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    /// <summary>
    /// Represents a list of dashboards.
    /// </summary>
    public sealed class DashboardList
    {
        /// <summary>
        /// Gets or sets the list of dashboards.
        /// </summary>
        [JsonPropertyName("items")]
        public List<PersesDashboard> Items { get; set; } = new();
    }

    /// <summary>
    /// Represents a list of datasources.
    /// </summary>
    public sealed class DatasourceList
    {
        /// <summary>
        /// Gets or sets the list of datasources.
        /// </summary>
        [JsonPropertyName("items")]
        public List<PersesDatasource> Items { get; set; } = new();
    }

    #endregion

    #region Response Models

    /// <summary>
    /// Represents an API error response.
    /// </summary>
    public sealed class PersesErrorResponse
    {
        /// <summary>
        /// Gets or sets the error message.
        /// </summary>
        [JsonPropertyName("error")]
        public string Error { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the error code.
        /// </summary>
        [JsonPropertyName("code")]
        public int? Code { get; set; }
    }

    #endregion
}
