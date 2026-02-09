using DataWarehouse.SDK.Contracts.DataLake;

namespace DataWarehouse.Plugins.UltimateDataLake.Strategies.Integration;

/// <summary>
/// Materialized View Strategy - Pre-computed views for warehouse.
/// Implements 112.8: Lake-to-warehouse integration.
/// </summary>
public sealed class MaterializedViewStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "materialized-view";
    public override string DisplayName => "Materialized Views";
    public override DataLakeCategory Category => DataLakeCategory.Integration;
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
        "Materialized views for pre-computing aggregations, joins, and transformations. " +
        "Supports incremental refresh, dependency tracking, and automatic invalidation.";
    public override string[] Tags => ["materialized-view", "aggregation", "incremental", "refresh"];
}

/// <summary>
/// Lake-Warehouse Sync Strategy - Bidirectional synchronization.
/// Implements 112.8: Lake-to-warehouse integration.
/// </summary>
public sealed class LakeWarehouseSyncStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "lake-warehouse-sync";
    public override string DisplayName => "Lake-Warehouse Sync";
    public override DataLakeCategory Category => DataLakeCategory.Integration;
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
        "Bidirectional synchronization between data lake and data warehouse with change data capture, " +
        "conflict resolution, and schema mapping for hybrid architectures.";
    public override string[] Tags => ["sync", "cdc", "bidirectional", "hybrid", "warehouse"];
}

/// <summary>
/// Query Federation Strategy - Unified query across lake and warehouse.
/// Implements 112.8: Lake-to-warehouse integration.
/// </summary>
public sealed class QueryFederationStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "query-federation";
    public override string DisplayName => "Query Federation";
    public override DataLakeCategory Category => DataLakeCategory.Integration;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsAcid = false,
        SupportsSchemaEvolution = false,
        SupportsTimeTravel = false,
        SupportedFormats = ["*"]
    };
    public override string SemanticDescription =>
        "Query federation enabling unified SQL queries across data lake and data warehouse tables. " +
        "Supports cross-system joins, predicate pushdown, and query optimization.";
    public override string[] Tags => ["federation", "unified-query", "cross-system", "join", "pushdown"];
}

/// <summary>
/// External Table Strategy - Warehouse external tables on lake data.
/// Implements 112.8: Lake-to-warehouse integration.
/// </summary>
public sealed class ExternalTableStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "external-table";
    public override string DisplayName => "External Tables";
    public override DataLakeCategory Category => DataLakeCategory.Integration;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsAcid = false,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = false,
        SupportedFormats = ["parquet", "orc", "avro", "json", "csv", "delta", "iceberg"]
    };
    public override string SemanticDescription =>
        "External tables in data warehouse pointing to data lake storage. Query lake data directly from warehouse " +
        "without data movement using formats like Parquet, ORC, and Delta Lake.";
    public override string[] Tags => ["external-table", "virtualization", "no-copy", "direct-access"];
}

/// <summary>
/// Data Virtualization Strategy - Virtual data layer.
/// Implements 112.8: Lake-to-warehouse integration.
/// </summary>
public sealed class DataVirtualizationStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "data-virtualization";
    public override string DisplayName => "Data Virtualization";
    public override DataLakeCategory Category => DataLakeCategory.Integration;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsAcid = false,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = false,
        SupportedFormats = ["*"]
    };
    public override string SemanticDescription =>
        "Data virtualization layer providing unified view of data across lake and warehouse without physical movement. " +
        "Includes semantic layer, business views, and query acceleration caching.";
    public override string[] Tags => ["virtualization", "semantic-layer", "unified-view", "abstraction"];
}

/// <summary>
/// Incremental Load Strategy - Efficient incremental data loading.
/// Implements 112.8: Lake-to-warehouse integration.
/// </summary>
public sealed class IncrementalLoadStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "incremental-load";
    public override string DisplayName => "Incremental Load";
    public override DataLakeCategory Category => DataLakeCategory.Integration;
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
        "Incremental data loading from lake to warehouse using change tracking, watermarks, and CDC. " +
        "Supports merge operations (upserts), SCD Type 1/2, and efficient delta processing.";
    public override string[] Tags => ["incremental", "cdc", "merge", "upsert", "scd", "delta"];
}

/// <summary>
/// Reverse ETL Strategy - Push data from warehouse to lake.
/// Implements 112.8: Lake-to-warehouse integration.
/// </summary>
public sealed class ReverseEtlStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "reverse-etl";
    public override string DisplayName => "Reverse ETL";
    public override DataLakeCategory Category => DataLakeCategory.Integration;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = true,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = false,
        SupportedFormats = ["parquet", "delta", "json", "csv"]
    };
    public override string SemanticDescription =>
        "Reverse ETL for pushing transformed data from warehouse back to data lake or operational systems. " +
        "Supports audience sync, ML feature stores, and data activation use cases.";
    public override string[] Tags => ["reverse-etl", "activation", "sync", "feature-store", "audience"];
}
