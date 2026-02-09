using DataWarehouse.SDK.Contracts.DataLake;

namespace DataWarehouse.Plugins.UltimateDataLake.Strategies.Architecture;

/// <summary>
/// Lambda Architecture Strategy - Combines batch and stream processing layers.
/// Implements 112.1: Data lake architecture.
/// </summary>
public sealed class LambdaArchitectureStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "lambda-architecture";
    public override string DisplayName => "Lambda Architecture";
    public override DataLakeCategory Category => DataLakeCategory.Architecture;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = false,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = false,
        SupportedFormats = ["parquet", "avro", "json", "orc"]
    };
    public override string SemanticDescription =>
        "Lambda architecture combining batch layer for comprehensive data processing, speed layer for real-time views, " +
        "and serving layer for query responses. Ideal for use cases requiring both accurate historical analysis and real-time insights.";
    public override string[] Tags => ["lambda", "batch", "streaming", "real-time", "hybrid"];
}

/// <summary>
/// Kappa Architecture Strategy - Unified stream processing architecture.
/// Implements 112.1: Data lake architecture.
/// </summary>
public sealed class KappaArchitectureStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "kappa-architecture";
    public override string DisplayName => "Kappa Architecture";
    public override DataLakeCategory Category => DataLakeCategory.Architecture;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsStreaming = true,
        SupportsAcid = false,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = false,
        SupportedFormats = ["avro", "json", "protobuf"]
    };
    public override string SemanticDescription =>
        "Kappa architecture treating all data as streams, simplifying the Lambda model by removing the batch layer. " +
        "Uses a single stream processing engine for both real-time and historical data reprocessing.";
    public override string[] Tags => ["kappa", "streaming", "real-time", "simplified"];
}

/// <summary>
/// Delta Lake Architecture Strategy - ACID transactions on data lake.
/// Implements 112.1: Data lake architecture.
/// </summary>
public sealed class DeltaLakeArchitectureStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "delta-lake-architecture";
    public override string DisplayName => "Delta Lake Architecture";
    public override DataLakeCategory Category => DataLakeCategory.Architecture;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = true,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = true,
        SupportedFormats = ["delta", "parquet"]
    };
    public override string SemanticDescription =>
        "Delta Lake architecture providing ACID transactions, scalable metadata handling, time travel, " +
        "unified batch and streaming, and schema enforcement/evolution on top of data lake storage.";
    public override string[] Tags => ["delta-lake", "acid", "time-travel", "lakehouse", "databricks"];
}

/// <summary>
/// Apache Iceberg Architecture Strategy - Open table format for large analytics.
/// Implements 112.1: Data lake architecture.
/// </summary>
public sealed class IcebergArchitectureStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "iceberg-architecture";
    public override string DisplayName => "Apache Iceberg Architecture";
    public override DataLakeCategory Category => DataLakeCategory.Architecture;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = true,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = true,
        SupportedFormats = ["iceberg", "parquet", "avro", "orc"]
    };
    public override string SemanticDescription =>
        "Apache Iceberg table format providing ACID transactions, schema evolution, hidden partitioning, " +
        "time travel, and efficient metadata management for petabyte-scale data lakes.";
    public override string[] Tags => ["iceberg", "acid", "time-travel", "partitioning", "netflix"];
}

/// <summary>
/// Apache Hudi Architecture Strategy - Incremental data processing.
/// Implements 112.1: Data lake architecture.
/// </summary>
public sealed class HudiArchitectureStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "hudi-architecture";
    public override string DisplayName => "Apache Hudi Architecture";
    public override DataLakeCategory Category => DataLakeCategory.Architecture;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = true,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = true,
        SupportedFormats = ["hudi", "parquet"]
    };
    public override string SemanticDescription =>
        "Apache Hudi (Hadoop Upserts Deletes and Incrementals) providing record-level inserts, updates, and deletes, " +
        "incremental data processing, and near real-time analytics on data lake storage.";
    public override string[] Tags => ["hudi", "upsert", "incremental", "cdc", "uber"];
}

/// <summary>
/// Lakehouse Architecture Strategy - Unified data warehouse and data lake.
/// Implements 112.1: Data lake architecture.
/// </summary>
public sealed class LakehouseArchitectureStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "lakehouse-architecture";
    public override string DisplayName => "Lakehouse Architecture";
    public override DataLakeCategory Category => DataLakeCategory.Architecture;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = true,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = true,
        SupportedFormats = ["delta", "iceberg", "hudi", "parquet"]
    };
    public override string SemanticDescription =>
        "Lakehouse architecture combining the best of data warehouses (ACID, governance, performance) " +
        "with data lakes (low-cost storage, open formats, ML support) in a unified platform.";
    public override string[] Tags => ["lakehouse", "unified", "warehouse", "lake", "modern"];
}

/// <summary>
/// Data Mesh Architecture Strategy - Domain-oriented decentralized data.
/// Implements 112.1: Data lake architecture.
/// </summary>
public sealed class DataMeshArchitectureStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "data-mesh-architecture";
    public override string DisplayName => "Data Mesh Architecture";
    public override DataLakeCategory Category => DataLakeCategory.Architecture;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = true,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = true,
        SupportedFormats = ["parquet", "avro", "delta", "iceberg"]
    };
    public override string SemanticDescription =>
        "Data Mesh architecture with domain-oriented decentralized data ownership, data as a product, " +
        "self-serve data infrastructure as platform, and federated computational governance.";
    public override string[] Tags => ["data-mesh", "domain", "decentralized", "product", "federated"];
}

/// <summary>
/// Data Fabric Architecture Strategy - Intelligent data integration layer.
/// Implements 112.1: Data lake architecture.
/// </summary>
public sealed class DataFabricArchitectureStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "data-fabric-architecture";
    public override string DisplayName => "Data Fabric Architecture";
    public override DataLakeCategory Category => DataLakeCategory.Architecture;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = true,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = true,
        SupportedFormats = ["parquet", "avro", "json", "csv", "xml"]
    };
    public override string SemanticDescription =>
        "Data Fabric architecture providing an intelligent data integration layer using AI/ML for automated " +
        "data discovery, governance, and integration across diverse data sources and locations.";
    public override string[] Tags => ["data-fabric", "ai", "integration", "automation", "unified"];
}
