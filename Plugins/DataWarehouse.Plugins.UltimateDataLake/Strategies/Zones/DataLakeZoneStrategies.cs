using DataWarehouse.SDK.Contracts.DataLake;

namespace DataWarehouse.Plugins.UltimateDataLake.Strategies.Zones;

/// <summary>
/// Medallion Architecture Strategy - Bronze/Silver/Gold layers.
/// Implements 112.5: Data lake zones.
/// </summary>
public sealed class MedallionArchitectureStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "medallion-architecture";
    public override string DisplayName => "Medallion Architecture";
    public override DataLakeCategory Category => DataLakeCategory.Zones;
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
        "Medallion (multi-hop) architecture with Bronze (raw), Silver (cleansed), and Gold (aggregated) layers. " +
        "Provides progressive data refinement with clear quality gates between layers.";
    public override string[] Tags => ["medallion", "bronze", "silver", "gold", "multi-hop", "lakehouse"];
}

/// <summary>
/// Raw Zone Strategy - Landing zone for raw data.
/// Implements 112.5: Data lake zones.
/// </summary>
public sealed class RawZoneStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "raw-zone";
    public override string DisplayName => "Raw Zone (Landing)";
    public override DataLakeCategory Category => DataLakeCategory.Zones;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = false,
        SupportsSchemaEvolution = false,
        SupportsTimeTravel = true,
        SupportedFormats = ["json", "csv", "xml", "avro", "parquet", "binary"]
    };
    public override string SemanticDescription =>
        "Raw/landing zone for ingesting data exactly as received from source systems. Preserves original format " +
        "and schema for auditability and reprocessing. Minimal transformations applied.";
    public override string[] Tags => ["raw", "landing", "ingestion", "source", "preserve"];
}

/// <summary>
/// Curated Zone Strategy - Cleansed and validated data.
/// Implements 112.5: Data lake zones.
/// </summary>
public sealed class CuratedZoneStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "curated-zone";
    public override string DisplayName => "Curated Zone (Silver)";
    public override DataLakeCategory Category => DataLakeCategory.Zones;
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
        "Curated zone containing cleansed, deduplicated, and validated data. Applies data quality rules, " +
        "standardizes formats, and conforms to enterprise schema standards.";
    public override string[] Tags => ["curated", "silver", "cleansed", "validated", "standardized"];
}

/// <summary>
/// Consumption Zone Strategy - Business-ready aggregated data.
/// Implements 112.5: Data lake zones.
/// </summary>
public sealed class ConsumptionZoneStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "consumption-zone";
    public override string DisplayName => "Consumption Zone (Gold)";
    public override DataLakeCategory Category => DataLakeCategory.Zones;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsAcid = true,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = true,
        SupportedFormats = ["delta", "parquet", "iceberg"]
    };
    public override string SemanticDescription =>
        "Consumption/gold zone containing business-ready data products: aggregations, metrics, features, " +
        "and denormalized tables optimized for analytics and reporting use cases.";
    public override string[] Tags => ["consumption", "gold", "aggregated", "business-ready", "reporting"];
}

/// <summary>
/// Sandbox Zone Strategy - Experimentation and data science.
/// Implements 112.5: Data lake zones.
/// </summary>
public sealed class SandboxZoneStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "sandbox-zone";
    public override string DisplayName => "Sandbox Zone";
    public override DataLakeCategory Category => DataLakeCategory.Zones;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsAcid = true,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = true,
        SupportedFormats = ["*"]
    };
    public override string SemanticDescription =>
        "Sandbox zone for data exploration, experimentation, and development. Provides isolated environments " +
        "for data scientists with controlled access to production data copies.";
    public override string[] Tags => ["sandbox", "experimentation", "data-science", "development"];
}

/// <summary>
/// Zone Promotion Strategy - Move data between zones.
/// Implements 112.5: Data lake zones.
/// </summary>
public sealed class ZonePromotionStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "zone-promotion";
    public override string DisplayName => "Zone Promotion";
    public override DataLakeCategory Category => DataLakeCategory.Zones;
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
        "Orchestrates data promotion between zones with quality gates, schema validation, " +
        "and lineage tracking. Ensures data meets criteria before advancing to next zone.";
    public override string[] Tags => ["promotion", "quality-gate", "pipeline", "orchestration"];
}

/// <summary>
/// Archive Zone Strategy - Long-term cold storage.
/// Implements 112.5: Data lake zones.
/// </summary>
public sealed class ArchiveZoneStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "archive-zone";
    public override string DisplayName => "Archive Zone";
    public override DataLakeCategory Category => DataLakeCategory.Zones;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsAcid = false,
        SupportsSchemaEvolution = false,
        SupportsTimeTravel = true,
        SupportedFormats = ["parquet", "orc", "avro"]
    };
    public override string SemanticDescription =>
        "Archive zone for historical data with infrequent access. Uses cost-optimized cold storage tiers " +
        "with configurable retention policies and compliance holds.";
    public override string[] Tags => ["archive", "cold-storage", "historical", "retention", "compliance"];
}
