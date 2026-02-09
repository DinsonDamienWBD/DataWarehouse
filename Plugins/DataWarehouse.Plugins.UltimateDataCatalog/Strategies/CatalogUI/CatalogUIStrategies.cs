namespace DataWarehouse.Plugins.UltimateDataCatalog.Strategies.CatalogUI;

/// <summary>
/// Asset Browser Strategy - Browse and explore assets.
/// Implements T128.8: Catalog UI components.
/// </summary>
public sealed class AssetBrowserStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "asset-browser";
    public override string DisplayName => "Asset Browser";
    public override DataCatalogCategory Category => DataCatalogCategory.CatalogUI;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsRealTime = true,
        SupportsFederation = true,
        SupportsVersioning = false,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Interactive asset browser with tree navigation, grid/list views, quick filters, and preview panes. " +
        "Supports keyboard shortcuts and bulk selection.";
    public override string[] Tags => ["browser", "navigation", "tree-view", "grid", "preview"];
}

/// <summary>
/// Schema Viewer Strategy - View schema details.
/// Implements T128.8: Catalog UI components.
/// </summary>
public sealed class SchemaViewerStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "schema-viewer";
    public override string DisplayName => "Schema Viewer";
    public override DataCatalogCategory Category => DataCatalogCategory.CatalogUI;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsRealTime = true,
        SupportsFederation = true,
        SupportsVersioning = true,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Rich schema viewer showing columns, data types, constraints, and sample data. " +
        "Supports schema comparison, version diff, and DDL generation.";
    public override string[] Tags => ["schema", "viewer", "columns", "ddl", "comparison"];
}

/// <summary>
/// Lineage Visualizer Strategy - Interactive lineage visualization.
/// Implements T128.8: Catalog UI components.
/// </summary>
public sealed class LineageVisualizerStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "lineage-visualizer";
    public override string DisplayName => "Lineage Visualizer";
    public override DataCatalogCategory Category => DataCatalogCategory.CatalogUI;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsRealTime = true,
        SupportsFederation = true,
        SupportsVersioning = true,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Interactive graph visualization of data lineage with zoom, pan, filtering, and drill-down. " +
        "Shows column-level lineage with transformation details.";
    public override string[] Tags => ["lineage", "graph", "visualization", "interactive", "column-level"];
}

/// <summary>
/// Data Profile Dashboard Strategy - Data profiling results.
/// Implements T128.8: Catalog UI components.
/// </summary>
public sealed class DataProfileDashboardStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "data-profile-dashboard";
    public override string DisplayName => "Data Profile Dashboard";
    public override DataCatalogCategory Category => DataCatalogCategory.CatalogUI;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsRealTime = true,
        SupportsFederation = true,
        SupportsVersioning = true,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Dashboard showing data profiling results including null rates, cardinality, distributions, " +
        "patterns, and anomalies with trend charts and alerts.";
    public override string[] Tags => ["profiling", "dashboard", "statistics", "distributions", "anomalies"];
}

/// <summary>
/// Search Interface Strategy - Advanced search UI.
/// Implements T128.8: Catalog UI components.
/// </summary>
public sealed class SearchInterfaceStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "search-interface";
    public override string DisplayName => "Search Interface";
    public override DataCatalogCategory Category => DataCatalogCategory.CatalogUI;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsRealTime = true,
        SupportsFederation = true,
        SupportsVersioning = false,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Advanced search interface with autocomplete, faceted filters, saved searches, and search history. " +
        "Supports natural language queries and query builder.";
    public override string[] Tags => ["search", "autocomplete", "facets", "filters", "query-builder"];
}

/// <summary>
/// Data Quality Scorecard Strategy - Quality metrics display.
/// Implements T128.8: Catalog UI components.
/// </summary>
public sealed class DataQualityScorecardStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "data-quality-scorecard";
    public override string DisplayName => "Data Quality Scorecard";
    public override DataCatalogCategory Category => DataCatalogCategory.CatalogUI;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsRealTime = true,
        SupportsFederation = true,
        SupportsVersioning = true,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Visual scorecard showing data quality metrics with traffic-light indicators, trend sparklines, " +
        "SLA compliance status, and drill-down to failed rules.";
    public override string[] Tags => ["quality", "scorecard", "metrics", "sla", "compliance"];
}

/// <summary>
/// Catalog Admin Console Strategy - Administration interface.
/// Implements T128.8: Catalog UI components.
/// </summary>
public sealed class CatalogAdminConsoleStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "catalog-admin-console";
    public override string DisplayName => "Catalog Admin Console";
    public override DataCatalogCategory Category => DataCatalogCategory.CatalogUI;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsFederation = true,
        SupportsVersioning = true,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Administration console for catalog configuration, user management, crawler scheduling, " +
        "policy management, and system health monitoring.";
    public override string[] Tags => ["admin", "console", "configuration", "monitoring", "management"];
}

/// <summary>
/// Glossary Editor Strategy - Business glossary editing UI.
/// Implements T128.8: Catalog UI components.
/// </summary>
public sealed class GlossaryEditorStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "glossary-editor";
    public override string DisplayName => "Glossary Editor";
    public override DataCatalogCategory Category => DataCatalogCategory.CatalogUI;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsRealTime = true,
        SupportsFederation = true,
        SupportsVersioning = true,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Rich editor for creating and managing business glossary terms with relationships, " +
        "synonyms, approval workflows, and term-to-asset linking.";
    public override string[] Tags => ["glossary", "editor", "terms", "relationships", "workflows"];
}

/// <summary>
/// Catalog Mobile App Strategy - Mobile-friendly interface.
/// Implements T128.8: Catalog UI components.
/// </summary>
public sealed class CatalogMobileAppStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "catalog-mobile-app";
    public override string DisplayName => "Catalog Mobile App";
    public override DataCatalogCategory Category => DataCatalogCategory.CatalogUI;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsRealTime = true,
        SupportsFederation = true,
        SupportsVersioning = false,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Mobile-responsive web app and native iOS/Android apps for catalog access on-the-go. " +
        "Supports search, browse, approve requests, and push notifications.";
    public override string[] Tags => ["mobile", "ios", "android", "responsive", "notifications"];
}

/// <summary>
/// Embedded Widget Strategy - Embeddable catalog widgets.
/// Implements T128.8: Catalog UI components.
/// </summary>
public sealed class EmbeddedWidgetStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "embedded-widget";
    public override string DisplayName => "Embedded Widgets";
    public override DataCatalogCategory Category => DataCatalogCategory.CatalogUI;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsRealTime = true,
        SupportsFederation = true,
        SupportsVersioning = false,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Embeddable widgets for integrating catalog search, asset cards, and lineage views into " +
        "BI tools, Jupyter notebooks, Confluence, and internal portals.";
    public override string[] Tags => ["widgets", "embedded", "iframe", "jupyter", "confluence"];
}
