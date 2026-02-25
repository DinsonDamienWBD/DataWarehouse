using DataWarehouse.SDK.Contracts.DataLake;

namespace DataWarehouse.Plugins.UltimateDataLake.Strategies.Catalog;

/// <summary>
/// Metadata Catalog Strategy - Core metadata management.
/// Implements 112.3: Data cataloging.
/// </summary>
public sealed class MetadataCatalogStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "metadata-catalog";
    public override string DisplayName => "Metadata Catalog";
    public override DataLakeCategory Category => DataLakeCategory.Catalog;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsAcid = true,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = true,
        SupportedFormats = ["json", "parquet", "delta"]
    };
    public override string SemanticDescription =>
        "Core metadata catalog for managing technical metadata (schemas, locations, formats), business metadata " +
        "(descriptions, owners, tags), and operational metadata (statistics, freshness, quality).";
    public override string[] Tags => ["metadata", "catalog", "technical", "business", "operational"];
}

/// <summary>
/// Data Discovery Strategy - Find and explore data assets.
/// Implements 112.3: Data cataloging.
/// </summary>
public sealed class DataDiscoveryStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "data-discovery";
    public override string DisplayName => "Data Discovery";
    public override DataLakeCategory Category => DataLakeCategory.Catalog;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsStreaming = false,
        SupportsAcid = false,
        SupportsSchemaEvolution = false,
        SupportsTimeTravel = false,
        SupportedFormats = ["*"]
    };
    public override string SemanticDescription =>
        "Automated data discovery through full-text search, semantic search, faceted navigation, " +
        "and AI-powered recommendations for finding relevant datasets across the data lake.";
    public override string[] Tags => ["discovery", "search", "semantic", "recommendations", "navigation"];
}

/// <summary>
/// Business Glossary Strategy - Business term definitions.
/// Implements 112.3: Data cataloging.
/// </summary>
public sealed class BusinessGlossaryStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "business-glossary";
    public override string DisplayName => "Business Glossary";
    public override DataLakeCategory Category => DataLakeCategory.Catalog;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsAcid = true,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = true,
        SupportedFormats = ["json"]
    };
    public override string SemanticDescription =>
        "Business glossary for defining and managing business terms, linking them to technical assets, " +
        "and providing consistent business context across the organization.";
    public override string[] Tags => ["glossary", "business-terms", "definitions", "context"];
}

/// <summary>
/// Tag Management Strategy - Classification and tagging.
/// Implements 112.3: Data cataloging.
/// </summary>
public sealed class TagManagementStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "tag-management";
    public override string DisplayName => "Tag Management";
    public override DataLakeCategory Category => DataLakeCategory.Catalog;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsAcid = true,
        SupportsSchemaEvolution = false,
        SupportsTimeTravel = false,
        SupportedFormats = ["*"]
    };
    public override string SemanticDescription =>
        "Flexible tagging system for classifying data assets with custom taxonomies, sensitivity labels, " +
        "data domains, and hierarchical tag structures with inheritance.";
    public override string[] Tags => ["tagging", "classification", "taxonomy", "labels", "hierarchy"];
}

/// <summary>
/// Hive Metastore Strategy - Hive-compatible catalog.
/// Implements 112.3: Data cataloging.
/// </summary>
public sealed class HiveMetastoreStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "hive-metastore";
    public override string DisplayName => "Hive Metastore";
    public override DataLakeCategory Category => DataLakeCategory.Catalog;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsAcid = true,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = false,
        SupportedFormats = ["parquet", "orc", "avro", "json", "csv"]
    };
    public override string SemanticDescription =>
        "Hive Metastore compatible catalog for storing table definitions, partitions, and statistics. " +
        "Integrates with Spark, Presto, Trino, and other big data engines.";
    public override string[] Tags => ["hive", "metastore", "spark", "presto", "trino"];
}

/// <summary>
/// AWS Glue Catalog Strategy - AWS Glue Data Catalog integration.
/// Implements 112.3: Data cataloging.
/// </summary>
public sealed class AwsGlueCatalogStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "aws-glue-catalog";
    public override string DisplayName => "AWS Glue Data Catalog";
    public override DataLakeCategory Category => DataLakeCategory.Catalog;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsAcid = true,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = false,
        SupportedFormats = ["parquet", "orc", "avro", "json", "csv", "iceberg"]
    };
    public override string SemanticDescription =>
        "Integration with AWS Glue Data Catalog for centralized metadata management, ETL job catalog, " +
        "crawler automation, and cross-service compatibility with Athena, Redshift, and EMR.";
    public override string[] Tags => ["aws", "glue", "athena", "redshift", "emr", "cloud"];
}

/// <summary>
/// Unity Catalog Strategy - Databricks Unity Catalog integration.
/// Implements 112.3: Data cataloging.
/// </summary>
public sealed class UnityCatalogStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "unity-catalog";
    public override string DisplayName => "Databricks Unity Catalog";
    public override DataLakeCategory Category => DataLakeCategory.Catalog;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = true,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = true,
        SupportedFormats = ["delta", "parquet", "iceberg"]
    };
    public override string SemanticDescription =>
        "Integration with Databricks Unity Catalog for unified governance across workspaces, " +
        "fine-grained access control, data lineage, and audit logging.";
    public override string[] Tags => ["databricks", "unity", "governance", "lakehouse", "cloud"];
}
