namespace DataWarehouse.Plugins.UltimateDataCatalog.Strategies.AssetDiscovery;

/// <summary>
/// Automated Data Asset Crawler Strategy - Automatically discovers data assets.
/// Implements T128.1: Asset discovery.
/// </summary>
public sealed class AutomatedCrawlerStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "automated-crawler";
    public override string DisplayName => "Automated Data Asset Crawler";
    public override DataCatalogCategory Category => DataCatalogCategory.AssetDiscovery;
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
        "Automated crawling engine that discovers data assets across databases, file systems, cloud storage, " +
        "and APIs. Extracts metadata, profiles data, and registers assets in the catalog automatically.";
    public override string[] Tags => ["crawler", "discovery", "automation", "metadata-extraction", "profiling"];
}

/// <summary>
/// Database Schema Scanner Strategy - Scans database schemas for assets.
/// Implements T128.1: Asset discovery.
/// </summary>
public sealed class DatabaseSchemaScannerStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "database-schema-scanner";
    public override string DisplayName => "Database Schema Scanner";
    public override DataCatalogCategory Category => DataCatalogCategory.AssetDiscovery;
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
        "Scans relational databases to discover tables, views, stored procedures, and functions. " +
        "Extracts schema definitions, constraints, indexes, and relationships from system catalogs.";
    public override string[] Tags => ["database", "schema", "scanner", "tables", "views", "procedures"];
}

/// <summary>
/// File System Scanner Strategy - Discovers assets in file systems.
/// Implements T128.1: Asset discovery.
/// </summary>
public sealed class FileSystemScannerStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "filesystem-scanner";
    public override string DisplayName => "File System Scanner";
    public override DataCatalogCategory Category => DataCatalogCategory.AssetDiscovery;
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
        "Scans local and network file systems to discover data files including CSV, JSON, Parquet, " +
        "Avro, ORC, and Excel. Infers schemas from file headers and samples.";
    public override string[] Tags => ["filesystem", "files", "csv", "parquet", "json", "scanner"];
}

/// <summary>
/// Cloud Storage Scanner Strategy - Discovers assets in cloud storage.
/// Implements T128.1: Asset discovery.
/// </summary>
public sealed class CloudStorageScannerStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "cloud-storage-scanner";
    public override string DisplayName => "Cloud Storage Scanner";
    public override DataCatalogCategory Category => DataCatalogCategory.AssetDiscovery;
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
        "Discovers data assets in cloud object storage including AWS S3, Azure Blob, Google Cloud Storage, " +
        "and MinIO. Scans buckets, prefixes, and objects with metadata extraction.";
    public override string[] Tags => ["cloud", "s3", "azure-blob", "gcs", "object-storage", "scanner"];
}

/// <summary>
/// API Endpoint Discovery Strategy - Discovers data through APIs.
/// Implements T128.1: Asset discovery.
/// </summary>
public sealed class ApiEndpointDiscoveryStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "api-endpoint-discovery";
    public override string DisplayName => "API Endpoint Discovery";
    public override DataCatalogCategory Category => DataCatalogCategory.AssetDiscovery;
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
        "Discovers data assets exposed through REST APIs, GraphQL endpoints, and OData services. " +
        "Parses OpenAPI specs, introspects GraphQL schemas, and catalogs API data sources.";
    public override string[] Tags => ["api", "rest", "graphql", "odata", "openapi", "discovery"];
}

/// <summary>
/// Streaming Source Discovery Strategy - Discovers streaming data sources.
/// Implements T128.1: Asset discovery.
/// </summary>
public sealed class StreamingSourceDiscoveryStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "streaming-source-discovery";
    public override string DisplayName => "Streaming Source Discovery";
    public override DataCatalogCategory Category => DataCatalogCategory.AssetDiscovery;
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
        "Discovers streaming data sources including Kafka topics, Kinesis streams, Pulsar topics, " +
        "and event hubs. Extracts schema registry information and stream metadata.";
    public override string[] Tags => ["streaming", "kafka", "kinesis", "pulsar", "event-streams", "discovery"];
}

/// <summary>
/// Data Lake Discovery Strategy - Discovers assets in data lakes.
/// Implements T128.1: Asset discovery.
/// </summary>
public sealed class DataLakeDiscoveryStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "datalake-discovery";
    public override string DisplayName => "Data Lake Discovery";
    public override DataCatalogCategory Category => DataCatalogCategory.AssetDiscovery;
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
        "Discovers assets in data lake environments including Delta Lake, Apache Iceberg, and Apache Hudi tables. " +
        "Integrates with Hive Metastore, AWS Glue, and Unity Catalog.";
    public override string[] Tags => ["datalake", "delta", "iceberg", "hudi", "lakehouse", "discovery"];
}

/// <summary>
/// ML Model Registry Discovery Strategy - Discovers ML models.
/// Implements T128.1: Asset discovery.
/// </summary>
public sealed class MlModelRegistryDiscoveryStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "ml-model-registry-discovery";
    public override string DisplayName => "ML Model Registry Discovery";
    public override DataCatalogCategory Category => DataCatalogCategory.AssetDiscovery;
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
        "Discovers ML models from model registries including MLflow, SageMaker Model Registry, and Vertex AI. " +
        "Catalogs model artifacts, metrics, parameters, and input/output schemas.";
    public override string[] Tags => ["ml", "models", "mlflow", "sagemaker", "vertex-ai", "registry"];
}

/// <summary>
/// Feature Store Discovery Strategy - Discovers ML features.
/// Implements T128.1: Asset discovery.
/// </summary>
public sealed class FeatureStoreDiscoveryStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "feature-store-discovery";
    public override string DisplayName => "Feature Store Discovery";
    public override DataCatalogCategory Category => DataCatalogCategory.AssetDiscovery;
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
        "Discovers ML features from feature stores including Feast, Tecton, and Databricks Feature Store. " +
        "Catalogs feature definitions, transformations, and serving configurations.";
    public override string[] Tags => ["features", "feast", "tecton", "feature-store", "ml", "discovery"];
}

/// <summary>
/// Incremental Change Detection Strategy - Detects changes since last scan.
/// Implements T128.1: Asset discovery.
/// </summary>
public sealed class IncrementalChangeDetectionStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "incremental-change-detection";
    public override string DisplayName => "Incremental Change Detection";
    public override DataCatalogCategory Category => DataCatalogCategory.AssetDiscovery;
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
        "Detects new, modified, and deleted assets since the last catalog scan using change data capture, " +
        "timestamps, and checksums. Enables efficient incremental catalog updates.";
    public override string[] Tags => ["incremental", "cdc", "change-detection", "delta", "efficient"];
}
