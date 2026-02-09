using DataWarehouse.SDK.Contracts.DataMesh;

namespace DataWarehouse.Plugins.UltimateDataMesh.Strategies.DataProduct;

/// <summary>
/// Data Product Definition Strategy - Defines data products with full specifications.
/// Implements T113.2: Data as a product.
/// </summary>
public sealed class DataProductDefinitionStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "data-product-definition";
    public override string DisplayName => "Data Product Definition";
    public override DataMeshCategory Category => DataMeshCategory.DataProduct;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = true,
        SupportsEventDriven = true,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Comprehensive data product definition including schema, SLA, quality metrics, access patterns, " +
        "and documentation. Treats data as a first-class product with clear ownership and contracts.";
    public override string[] Tags => ["product", "definition", "schema", "contract", "specification"];
}

/// <summary>
/// Data Product SLA Strategy - Defines and enforces service level agreements.
/// Implements T113.2: Data as a product.
/// </summary>
public sealed class DataProductSlaStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "data-product-sla";
    public override string DisplayName => "Data Product SLA";
    public override DataMeshCategory Category => DataMeshCategory.DataProduct;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = true,
        SupportsEventDriven = true,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Service Level Agreement management for data products including availability targets, latency guarantees, " +
        "throughput limits, data freshness SLOs, and automated SLA violation detection and alerting.";
    public override string[] Tags => ["sla", "availability", "latency", "throughput", "slo"];
}

/// <summary>
/// Data Product Versioning Strategy - Manages data product versions and migrations.
/// Implements T113.2: Data as a product.
/// </summary>
public sealed class DataProductVersioningStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "data-product-versioning";
    public override string DisplayName => "Data Product Versioning";
    public override DataMeshCategory Category => DataMeshCategory.DataProduct;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = false,
        SupportsBatch = true,
        SupportsEventDriven = true,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Semantic versioning for data products with backward/forward compatibility checks, migration paths, " +
        "deprecation policies, and consumer notification for breaking changes.";
    public override string[] Tags => ["versioning", "semver", "migration", "deprecation", "compatibility"];
}

/// <summary>
/// Data Product Quality Strategy - Ensures data product quality standards.
/// Implements T113.2: Data as a product.
/// </summary>
public sealed class DataProductQualityStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "data-product-quality";
    public override string DisplayName => "Data Product Quality";
    public override DataMeshCategory Category => DataMeshCategory.DataProduct;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = true,
        SupportsEventDriven = true,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Data quality assurance for products including completeness, accuracy, consistency, timeliness, " +
        "and validity checks. Supports quality tiers (Bronze/Silver/Gold/Platinum) with automated scoring.";
    public override string[] Tags => ["quality", "completeness", "accuracy", "consistency", "tiering"];
}

/// <summary>
/// Data Product Documentation Strategy - Auto-generates and maintains documentation.
/// Implements T113.2: Data as a product.
/// </summary>
public sealed class DataProductDocumentationStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "data-product-documentation";
    public override string DisplayName => "Data Product Documentation";
    public override DataMeshCategory Category => DataMeshCategory.DataProduct;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = false,
        SupportsBatch = true,
        SupportsEventDriven = true,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Automated documentation generation for data products including schema docs, usage examples, " +
        "data dictionaries, lineage diagrams, and interactive API documentation.";
    public override string[] Tags => ["documentation", "data-dictionary", "examples", "api-docs", "lineage"];
}

/// <summary>
/// Data Product Consumption Strategy - Manages data product consumption patterns.
/// Implements T113.2: Data as a product.
/// </summary>
public sealed class DataProductConsumptionStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "data-product-consumption";
    public override string DisplayName => "Data Product Consumption";
    public override DataMeshCategory Category => DataMeshCategory.DataProduct;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = true,
        SupportsEventDriven = true,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Manages data product consumption including subscription management, access patterns, rate limiting, " +
        "quota enforcement, and consumer analytics for product improvement.";
    public override string[] Tags => ["consumption", "subscription", "rate-limiting", "quota", "analytics"];
}

/// <summary>
/// Data Product Feedback Strategy - Collects and processes consumer feedback.
/// Implements T113.2: Data as a product.
/// </summary>
public sealed class DataProductFeedbackStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "data-product-feedback";
    public override string DisplayName => "Data Product Feedback";
    public override DataMeshCategory Category => DataMeshCategory.DataProduct;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = false,
        SupportsEventDriven = true,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Consumer feedback collection and processing including quality ratings, feature requests, " +
        "issue reporting, and NPS scoring for continuous data product improvement.";
    public override string[] Tags => ["feedback", "ratings", "nps", "issues", "improvement"];
}
