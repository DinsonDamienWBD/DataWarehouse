using DataWarehouse.SDK.Contracts.DataLake;

namespace DataWarehouse.Plugins.UltimateDataLake.Strategies.Lineage;

/// <summary>
/// Column-Level Lineage Strategy - Track lineage at column granularity.
/// Implements 112.4: Data lineage tracking.
/// </summary>
public sealed class ColumnLevelLineageStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "column-level-lineage";
    public override string DisplayName => "Column-Level Lineage";
    public override DataLakeCategory Category => DataLakeCategory.Lineage;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = true,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = true,
        SupportedFormats = ["*"]
    };
    public override string SemanticDescription =>
        "Fine-grained column-level lineage tracking showing how each column is derived from source columns, " +
        "including transformations, expressions, and aggregations applied during data processing.";
    public override string[] Tags => ["column-lineage", "fine-grained", "transformation", "expression"];
}

/// <summary>
/// Table-Level Lineage Strategy - Track lineage at table/dataset level.
/// Implements 112.4: Data lineage tracking.
/// </summary>
public sealed class TableLevelLineageStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "table-level-lineage";
    public override string DisplayName => "Table-Level Lineage";
    public override DataLakeCategory Category => DataLakeCategory.Lineage;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = true,
        SupportsSchemaEvolution = false,
        SupportsTimeTravel = true,
        SupportedFormats = ["*"]
    };
    public override string SemanticDescription =>
        "Table-level lineage tracking showing dependencies between tables/datasets, pipeline relationships, " +
        "and data flow across the data lake with job-level attribution.";
    public override string[] Tags => ["table-lineage", "dataset", "pipeline", "dependencies"];
}

/// <summary>
/// SQL Parser Lineage Strategy - Extract lineage from SQL queries.
/// Implements 112.4: Data lineage tracking.
/// </summary>
public sealed class SqlParserLineageStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "sql-parser-lineage";
    public override string DisplayName => "SQL Parser Lineage";
    public override DataLakeCategory Category => DataLakeCategory.Lineage;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsAcid = false,
        SupportsSchemaEvolution = false,
        SupportsTimeTravel = false,
        SupportedFormats = ["sql"]
    };
    public override string SemanticDescription =>
        "Extracts data lineage by parsing SQL queries, CTEs, views, and stored procedures to identify " +
        "source-to-target mappings without requiring runtime instrumentation.";
    public override string[] Tags => ["sql-parser", "static-analysis", "cte", "views"];
}

/// <summary>
/// Spark Lineage Strategy - Capture lineage from Spark jobs.
/// Implements 112.4: Data lineage tracking.
/// </summary>
public sealed class SparkLineageStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "spark-lineage";
    public override string DisplayName => "Apache Spark Lineage";
    public override DataLakeCategory Category => DataLakeCategory.Lineage;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = false,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = false,
        SupportedFormats = ["parquet", "delta", "orc", "avro", "json", "csv"]
    };
    public override string SemanticDescription =>
        "Captures lineage from Apache Spark jobs by instrumenting Spark listeners, parsing logical plans, " +
        "and tracking DataFrames and RDDs through transformations.";
    public override string[] Tags => ["spark", "dataframe", "rdd", "listener", "plan"];
}

/// <summary>
/// Impact Analysis Strategy - Analyze downstream impact of changes.
/// Implements 112.4: Data lineage tracking.
/// </summary>
public sealed class ImpactAnalysisStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "impact-analysis";
    public override string DisplayName => "Impact Analysis";
    public override DataLakeCategory Category => DataLakeCategory.Lineage;
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
        "Performs impact analysis to identify all downstream datasets, reports, and applications " +
        "affected by changes to a source table or column, supporting change management.";
    public override string[] Tags => ["impact-analysis", "downstream", "change-management", "dependency"];
}

/// <summary>
/// Root Cause Analysis Strategy - Trace issues to source.
/// Implements 112.4: Data lineage tracking.
/// </summary>
public sealed class RootCauseAnalysisStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "root-cause-analysis";
    public override string DisplayName => "Root Cause Analysis";
    public override DataLakeCategory Category => DataLakeCategory.Lineage;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsAcid = false,
        SupportsSchemaEvolution = false,
        SupportsTimeTravel = true,
        SupportedFormats = ["*"]
    };
    public override string SemanticDescription =>
        "Traces data quality issues and anomalies back to their source through upstream lineage traversal, " +
        "identifying the root cause of data problems for rapid remediation.";
    public override string[] Tags => ["root-cause", "upstream", "troubleshooting", "debugging"];
}

/// <summary>
/// OpenLineage Strategy - OpenLineage standard integration.
/// Implements 112.4: Data lineage tracking.
/// </summary>
public sealed class OpenLineageStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "openlineage";
    public override string DisplayName => "OpenLineage";
    public override DataLakeCategory Category => DataLakeCategory.Lineage;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = false,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = false,
        SupportedFormats = ["*"]
    };
    public override string SemanticDescription =>
        "Implementation of the OpenLineage standard for cross-platform lineage interoperability, " +
        "enabling lineage collection from Airflow, Spark, dbt, and other supported tools.";
    public override string[] Tags => ["openlineage", "standard", "interoperability", "airflow", "dbt"];
}
