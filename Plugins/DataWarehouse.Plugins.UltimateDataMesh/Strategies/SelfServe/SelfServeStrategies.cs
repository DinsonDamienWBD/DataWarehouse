using DataWarehouse.SDK.Contracts.DataMesh;

namespace DataWarehouse.Plugins.UltimateDataMesh.Strategies.SelfServe;

/// <summary>
/// Self-Service Data Platform Strategy - Enables domain teams to self-serve.
/// Implements T113.3: Self-serve data infrastructure.
/// </summary>
public sealed class SelfServiceDataPlatformStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "self-service-data-platform";
    public override string DisplayName => "Self-Service Data Platform";
    public override DataMeshCategory Category => DataMeshCategory.SelfServe;
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
        "Complete self-service data platform enabling domain teams to provision infrastructure, " +
        "create data products, and manage consumers without central IT dependencies.";
    public override string[] Tags => ["self-service", "platform", "provisioning", "automation", "portal"];
}

/// <summary>
/// Infrastructure as Code Strategy - Declarative infrastructure provisioning.
/// Implements T113.3: Self-serve data infrastructure.
/// </summary>
public sealed class InfrastructureAsCodeStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "infrastructure-as-code";
    public override string DisplayName => "Infrastructure as Code";
    public override DataMeshCategory Category => DataMeshCategory.SelfServe;
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
        "Infrastructure as Code for data mesh resources using declarative specifications (YAML/JSON). " +
        "Supports GitOps workflows, version control, and automated rollback.";
    public override string[] Tags => ["iac", "declarative", "yaml", "gitops", "terraform"];
}

/// <summary>
/// Data Product Template Strategy - Reusable templates for data products.
/// Implements T113.3: Self-serve data infrastructure.
/// </summary>
public sealed class DataProductTemplateStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "data-product-template";
    public override string DisplayName => "Data Product Templates";
    public override DataMeshCategory Category => DataMeshCategory.SelfServe;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = false,
        SupportsBatch = true,
        SupportsEventDriven = false,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Reusable data product templates with pre-configured schemas, quality rules, access patterns, " +
        "and documentation. Accelerates data product creation with best-practice defaults.";
    public override string[] Tags => ["templates", "scaffolding", "blueprints", "best-practices", "starter"];
}

/// <summary>
/// Automated Pipeline Strategy - Self-service data pipeline creation.
/// Implements T113.3: Self-serve data infrastructure.
/// </summary>
public sealed class AutomatedPipelineStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "automated-pipeline";
    public override string DisplayName => "Automated Pipelines";
    public override DataMeshCategory Category => DataMeshCategory.SelfServe;
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
        "Automated data pipeline provisioning with drag-and-drop pipeline builder, " +
        "pre-built connectors, and automated orchestration for ETL/ELT workflows.";
    public override string[] Tags => ["pipeline", "etl", "orchestration", "connectors", "workflow"];
}

/// <summary>
/// Self-Service Analytics Strategy - Self-service analytics and BI capabilities.
/// Implements T113.3: Self-serve data infrastructure.
/// </summary>
public sealed class SelfServiceAnalyticsStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "self-service-analytics";
    public override string DisplayName => "Self-Service Analytics";
    public override DataMeshCategory Category => DataMeshCategory.SelfServe;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = true,
        SupportsEventDriven = false,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Self-service analytics with query builder, visualization tools, dashboard creation, " +
        "and ad-hoc analysis capabilities without requiring data engineering support.";
    public override string[] Tags => ["analytics", "bi", "visualization", "dashboard", "query"];
}

/// <summary>
/// API Portal Strategy - Self-service API portal for data access.
/// Implements T113.3: Self-serve data infrastructure.
/// </summary>
public sealed class ApiPortalStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "api-portal";
    public override string DisplayName => "API Portal";
    public override DataMeshCategory Category => DataMeshCategory.SelfServe;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = true,
        SupportsEventDriven = false,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Self-service API portal for data product discovery, subscription, and access management. " +
        "Includes interactive documentation, sandbox testing, and API key management.";
    public override string[] Tags => ["api", "portal", "discovery", "subscription", "sandbox"];
}

/// <summary>
/// Resource Quota Strategy - Self-service resource quota management.
/// Implements T113.3: Self-serve data infrastructure.
/// </summary>
public sealed class ResourceQuotaStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "resource-quota";
    public override string DisplayName => "Resource Quotas";
    public override DataMeshCategory Category => DataMeshCategory.SelfServe;
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
        "Self-service resource quota management with configurable limits for storage, compute, " +
        "API calls, and bandwidth. Includes usage dashboards and automated scaling requests.";
    public override string[] Tags => ["quota", "limits", "usage", "scaling", "cost-management"];
}
