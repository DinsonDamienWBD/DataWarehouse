using DataWarehouse.SDK.Contracts.DataLake;

namespace DataWarehouse.Plugins.UltimateDataLake.Strategies.Schema;

/// <summary>
/// Dynamic Schema Inference Strategy - Automatically infer schema from data.
/// Implements 112.2: Schema-on-read support.
/// </summary>
public sealed class DynamicSchemaInferenceStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "dynamic-schema-inference";
    public override string DisplayName => "Dynamic Schema Inference";
    public override DataLakeCategory Category => DataLakeCategory.Schema;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsAcid = false,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = false,
        SupportedFormats = ["json", "csv", "avro", "parquet", "xml"]
    };
    public override string SemanticDescription =>
        "Automatically infers schema from data at read time by sampling files, detecting data types, " +
        "identifying nullable columns, and handling nested structures. Supports JSON, CSV, Avro, and Parquet.";
    public override string[] Tags => ["schema-inference", "dynamic", "auto-detect", "sampling"];
}

/// <summary>
/// Schema Evolution Strategy - Handle schema changes over time.
/// Implements 112.2: Schema-on-read support.
/// </summary>
public sealed class SchemaEvolutionStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "schema-evolution";
    public override string DisplayName => "Schema Evolution";
    public override DataLakeCategory Category => DataLakeCategory.Schema;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = true,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = true,
        SupportedFormats = ["delta", "iceberg", "hudi", "avro"]
    };
    public override string SemanticDescription =>
        "Manages schema evolution with support for adding/removing columns, changing data types, " +
        "renaming columns, and reordering columns while maintaining backward and forward compatibility.";
    public override string[] Tags => ["schema-evolution", "compatibility", "versioning", "migration"];
}

/// <summary>
/// Schema Merge Strategy - Merge schemas from multiple sources.
/// Implements 112.2: Schema-on-read support.
/// </summary>
public sealed class SchemaMergeStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "schema-merge";
    public override string DisplayName => "Schema Merge";
    public override DataLakeCategory Category => DataLakeCategory.Schema;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsAcid = false,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = false,
        SupportedFormats = ["parquet", "avro", "json"]
    };
    public override string SemanticDescription =>
        "Merges schemas from multiple files or partitions into a unified schema, handling column additions, " +
        "type promotions (int to long), and nullable vs non-nullable conflicts.";
    public override string[] Tags => ["schema-merge", "union", "consolidation", "multi-source"];
}

/// <summary>
/// Schema Validation Strategy - Validate data against schema.
/// Implements 112.2: Schema-on-read support.
/// </summary>
public sealed class SchemaValidationStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "schema-validation";
    public override string DisplayName => "Schema Validation";
    public override DataLakeCategory Category => DataLakeCategory.Schema;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = false,
        SupportsSchemaEvolution = false,
        SupportsTimeTravel = false,
        SupportedFormats = ["parquet", "avro", "json", "csv"]
    };
    public override string SemanticDescription =>
        "Validates incoming data against expected schema, checking data types, constraints, required fields, " +
        "and custom validation rules. Reports violations with detailed error messages.";
    public override string[] Tags => ["schema-validation", "quality", "constraints", "enforcement"];
}

/// <summary>
/// Schema Registry Integration Strategy - Connect to schema registries.
/// Implements 112.2: Schema-on-read support.
/// </summary>
public sealed class SchemaRegistryIntegrationStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "schema-registry-integration";
    public override string DisplayName => "Schema Registry Integration";
    public override DataLakeCategory Category => DataLakeCategory.Schema;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = false,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = false,
        SupportedFormats = ["avro", "protobuf", "json"]
    };
    public override string SemanticDescription =>
        "Integrates with external schema registries (Confluent, AWS Glue, Azure Schema Registry) for " +
        "centralized schema management, versioning, and compatibility checking.";
    public override string[] Tags => ["schema-registry", "confluent", "glue", "centralized"];
}

/// <summary>
/// Polyglot Schema Strategy - Handle multiple schema formats.
/// Implements 112.2: Schema-on-read support.
/// </summary>
public sealed class PolyglotSchemaStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "polyglot-schema";
    public override string DisplayName => "Polyglot Schema";
    public override DataLakeCategory Category => DataLakeCategory.Schema;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsAcid = false,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = false,
        SupportedFormats = ["avro", "protobuf", "json-schema", "thrift", "parquet"]
    };
    public override string SemanticDescription =>
        "Unified schema handling across multiple formats: Avro, Protocol Buffers, JSON Schema, Thrift, " +
        "and Parquet schemas with cross-format translation and compatibility checking.";
    public override string[] Tags => ["polyglot", "multi-format", "translation", "interoperability"];
}
