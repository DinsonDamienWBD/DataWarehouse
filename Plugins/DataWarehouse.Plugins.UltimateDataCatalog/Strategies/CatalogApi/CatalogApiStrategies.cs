namespace DataWarehouse.Plugins.UltimateDataCatalog.Strategies.CatalogApi;

/// <summary>
/// REST API Strategy - RESTful catalog API.
/// Implements T128.7: Catalog APIs.
/// </summary>
public sealed class RestApiStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "rest-api";
    public override string DisplayName => "REST API";
    public override DataCatalogCategory Category => DataCatalogCategory.CatalogApi;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsFederation = false,
        SupportsVersioning = true,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "RESTful API following OpenAPI 3.0 specification for catalog operations including CRUD, search, " +
        "lineage queries, and bulk operations with pagination and filtering.";
    public override string[] Tags => ["rest", "openapi", "http", "json", "crud"];
}

/// <summary>
/// GraphQL API Strategy - GraphQL catalog interface.
/// Implements T128.7: Catalog APIs.
/// </summary>
public sealed class GraphQlApiStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "graphql-api";
    public override string DisplayName => "GraphQL API";
    public override DataCatalogCategory Category => DataCatalogCategory.CatalogApi;
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
        "GraphQL API for flexible catalog queries with nested relationships, selective field retrieval, " +
        "mutations, and subscriptions for real-time updates.";
    public override string[] Tags => ["graphql", "queries", "mutations", "subscriptions", "flexible"];
}

/// <summary>
/// Python SDK Strategy - Python client library.
/// Implements T128.7: Catalog APIs.
/// </summary>
public sealed class PythonSdkStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "python-sdk";
    public override string DisplayName => "Python SDK";
    public override DataCatalogCategory Category => DataCatalogCategory.CatalogApi;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = false,
        SupportsFederation = false,
        SupportsVersioning = true,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Python SDK for data scientists and engineers with pandas integration, Jupyter notebook support, " +
        "and async/await patterns. Available via pip install.";
    public override string[] Tags => ["python", "sdk", "pandas", "jupyter", "pip"];
}

/// <summary>
/// Java SDK Strategy - Java client library.
/// Implements T128.7: Catalog APIs.
/// </summary>
public sealed class JavaSdkStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "java-sdk";
    public override string DisplayName => "Java SDK";
    public override DataCatalogCategory Category => DataCatalogCategory.CatalogApi;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = false,
        SupportsFederation = false,
        SupportsVersioning = true,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Java SDK for enterprise applications and Spark jobs with builder patterns, fluent API, " +
        "and CompletableFuture async support. Available via Maven Central.";
    public override string[] Tags => ["java", "sdk", "maven", "spark", "enterprise"];
}

/// <summary>
/// CLI Tool Strategy - Command-line interface.
/// Implements T128.7: Catalog APIs.
/// </summary>
public sealed class CliToolStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "cli-tool";
    public override string DisplayName => "CLI Tool";
    public override DataCatalogCategory Category => DataCatalogCategory.CatalogApi;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = false,
        SupportsFederation = false,
        SupportsVersioning = true,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Command-line interface for catalog operations in scripts and CI/CD pipelines. Supports " +
        "JSON/YAML output, configuration profiles, and shell completion.";
    public override string[] Tags => ["cli", "terminal", "scripts", "cicd", "automation"];
}

/// <summary>
/// Webhook Events Strategy - Event notifications via webhooks.
/// Implements T128.7: Catalog APIs.
/// </summary>
public sealed class WebhookEventsStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "webhook-events";
    public override string DisplayName => "Webhook Events";
    public override DataCatalogCategory Category => DataCatalogCategory.CatalogApi;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsFederation = false,
        SupportsVersioning = false,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Webhook notifications for catalog events (new assets, schema changes, access requests) " +
        "with configurable filters, retry logic, and delivery verification.";
    public override string[] Tags => ["webhooks", "events", "notifications", "real-time", "integration"];
}

/// <summary>
/// Event Streaming Strategy - Kafka/Kinesis event streams.
/// Implements T128.7: Catalog APIs.
/// </summary>
public sealed class EventStreamingStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "event-streaming";
    public override string DisplayName => "Event Streaming";
    public override DataCatalogCategory Category => DataCatalogCategory.CatalogApi;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsFederation = false,
        SupportsVersioning = false,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Publishes catalog events to Kafka/Kinesis streams for integration with data pipelines, " +
        "event-driven architectures, and real-time analytics systems.";
    public override string[] Tags => ["kafka", "kinesis", "streaming", "events", "cdc"];
}

/// <summary>
/// Bulk Import/Export Strategy - Batch data operations.
/// Implements T128.7: Catalog APIs.
/// </summary>
public sealed class BulkImportExportStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "bulk-import-export";
    public override string DisplayName => "Bulk Import/Export";
    public override DataCatalogCategory Category => DataCatalogCategory.CatalogApi;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = false,
        SupportsFederation = true,
        SupportsVersioning = true,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Bulk import and export of catalog metadata in JSON, CSV, and catalog interchange formats. " +
        "Supports migration between catalog systems and backup/restore.";
    public override string[] Tags => ["bulk", "import", "export", "migration", "backup"];
}

/// <summary>
/// API Gateway Integration Strategy - API management integration.
/// Implements T128.7: Catalog APIs.
/// </summary>
public sealed class ApiGatewayIntegrationStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "api-gateway-integration";
    public override string DisplayName => "API Gateway Integration";
    public override DataCatalogCategory Category => DataCatalogCategory.CatalogApi;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsFederation = false,
        SupportsVersioning = true,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Integration with API gateways (Kong, AWS API Gateway, Azure APIM) for rate limiting, " +
        "API key management, request validation, and API analytics.";
    public override string[] Tags => ["gateway", "kong", "rate-limiting", "api-keys", "throttling"];
}

/// <summary>
/// Third-Party Catalog Integration Strategy - Integrates with other catalogs.
/// Implements T128.7: Catalog APIs.
/// </summary>
public sealed class ThirdPartyCatalogIntegrationStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "third-party-catalog-integration";
    public override string DisplayName => "Third-Party Catalog Integration";
    public override DataCatalogCategory Category => DataCatalogCategory.CatalogApi;
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
        "Bidirectional integration with third-party catalogs (Collibra, Alation, Atlan, DataHub) " +
        "for metadata synchronization and federated search.";
    public override string[] Tags => ["collibra", "alation", "atlan", "datahub", "federation"];
}
