namespace DataWarehouse.Plugins.UltimateDataCatalog.Strategies.SchemaRegistry;

/// <summary>
/// Centralized Schema Registry Strategy - Central schema management.
/// Implements T128.2: Schema registry.
/// </summary>
public sealed class CentralizedSchemaRegistryStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "centralized-schema-registry";
    public override string DisplayName => "Centralized Schema Registry";
    public override DataCatalogCategory Category => DataCatalogCategory.SchemaRegistry;
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
        "Centralized schema registry for storing, versioning, and managing all data schemas. " +
        "Provides schema validation, compatibility checking, and schema lifecycle management.";
    public override string[] Tags => ["schema", "registry", "centralized", "versioning", "lifecycle"];
}

/// <summary>
/// Schema Version Control Strategy - Git-like schema versioning.
/// Implements T128.2: Schema registry.
/// </summary>
public sealed class SchemaVersionControlStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "schema-version-control";
    public override string DisplayName => "Schema Version Control";
    public override DataCatalogCategory Category => DataCatalogCategory.SchemaRegistry;
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
        "Git-like version control for schemas with branching, merging, and history tracking. " +
        "Supports schema evolution with forward and backward compatibility validation.";
    public override string[] Tags => ["version-control", "git", "branching", "merging", "history"];
}

/// <summary>
/// Schema Compatibility Checker Strategy - Validates schema compatibility.
/// Implements T128.2: Schema registry.
/// </summary>
public sealed class SchemaCompatibilityCheckerStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "schema-compatibility-checker";
    public override string DisplayName => "Schema Compatibility Checker";
    public override DataCatalogCategory Category => DataCatalogCategory.SchemaRegistry;
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
        "Validates schema compatibility between versions using rules like BACKWARD, FORWARD, FULL, and NONE. " +
        "Detects breaking changes and provides compatibility reports.";
    public override string[] Tags => ["compatibility", "validation", "backward", "forward", "breaking-changes"];
}

/// <summary>
/// Avro Schema Registry Strategy - Apache Avro schema management.
/// Implements T128.2: Schema registry.
/// </summary>
public sealed class AvroSchemaRegistryStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "avro-schema-registry";
    public override string DisplayName => "Avro Schema Registry";
    public override DataCatalogCategory Category => DataCatalogCategory.SchemaRegistry;
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
        "Schema registry for Apache Avro schemas with support for schema evolution, aliases, and defaults. " +
        "Compatible with Confluent Schema Registry API.";
    public override string[] Tags => ["avro", "confluent", "kafka", "serialization", "schema-evolution"];
}

/// <summary>
/// Protobuf Schema Registry Strategy - Protocol Buffers schema management.
/// Implements T128.2: Schema registry.
/// </summary>
public sealed class ProtobufSchemaRegistryStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "protobuf-schema-registry";
    public override string DisplayName => "Protobuf Schema Registry";
    public override DataCatalogCategory Category => DataCatalogCategory.SchemaRegistry;
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
        "Schema registry for Protocol Buffers with support for proto2 and proto3. " +
        "Manages message definitions, services, and RPC contracts.";
    public override string[] Tags => ["protobuf", "grpc", "proto3", "messages", "services"];
}

/// <summary>
/// JSON Schema Registry Strategy - JSON Schema management.
/// Implements T128.2: Schema registry.
/// </summary>
public sealed class JsonSchemaRegistryStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "json-schema-registry";
    public override string DisplayName => "JSON Schema Registry";
    public override DataCatalogCategory Category => DataCatalogCategory.SchemaRegistry;
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
        "Schema registry for JSON Schema with support for Draft-07, Draft-2019-09, and Draft-2020-12. " +
        "Validates JSON documents and manages schema references.";
    public override string[] Tags => ["json", "json-schema", "validation", "draft-2020", "references"];
}

/// <summary>
/// Schema Inference Engine Strategy - Automatic schema inference.
/// Implements T128.2: Schema registry.
/// </summary>
public sealed class SchemaInferenceEngineStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "schema-inference-engine";
    public override string DisplayName => "Schema Inference Engine";
    public override DataCatalogCategory Category => DataCatalogCategory.SchemaRegistry;
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
        "Automatically infers schemas from data samples using statistical analysis and ML-based type detection. " +
        "Handles nested structures, unions, and nullable fields.";
    public override string[] Tags => ["inference", "automatic", "ml", "type-detection", "sampling"];
}

/// <summary>
/// Schema Migration Generator Strategy - Generates schema migrations.
/// Implements T128.2: Schema registry.
/// </summary>
public sealed class SchemaMigrationGeneratorStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "schema-migration-generator";
    public override string DisplayName => "Schema Migration Generator";
    public override DataCatalogCategory Category => DataCatalogCategory.SchemaRegistry;
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
        "Generates migration scripts (DDL, DML) to transform data from one schema version to another. " +
        "Supports rollback scripts and incremental migrations.";
    public override string[] Tags => ["migration", "ddl", "transformation", "rollback", "upgrade"];
}

/// <summary>
/// Cross-Platform Schema Translator Strategy - Translates between schema formats.
/// Implements T128.2: Schema registry.
/// </summary>
public sealed class CrossPlatformSchemaTranslatorStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "cross-platform-schema-translator";
    public override string DisplayName => "Cross-Platform Schema Translator";
    public override DataCatalogCategory Category => DataCatalogCategory.SchemaRegistry;
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
        "Translates schemas between formats including Avro, Protobuf, JSON Schema, SQL DDL, and Parquet. " +
        "Maintains semantic equivalence during translation.";
    public override string[] Tags => ["translation", "conversion", "cross-platform", "interoperability"];
}

/// <summary>
/// Schema Governance Policy Strategy - Enforces schema governance policies.
/// Implements T128.2: Schema registry.
/// </summary>
public sealed class SchemaGovernancePolicyStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "schema-governance-policy";
    public override string DisplayName => "Schema Governance Policy";
    public override DataCatalogCategory Category => DataCatalogCategory.SchemaRegistry;
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
        "Enforces organizational policies on schema design including naming conventions, data types, " +
        "documentation requirements, and sensitive data classification.";
    public override string[] Tags => ["governance", "policy", "naming-conventions", "compliance", "standards"];
}
