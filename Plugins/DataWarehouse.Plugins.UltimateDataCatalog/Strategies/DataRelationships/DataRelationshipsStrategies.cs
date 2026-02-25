namespace DataWarehouse.Plugins.UltimateDataCatalog.Strategies.DataRelationships;

/// <summary>
/// Data Lineage Graph Strategy - Visual lineage relationships.
/// Implements T128.5: Data relationships.
/// </summary>
public sealed class DataLineageGraphStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "data-lineage-graph";
    public override string DisplayName => "Data Lineage Graph";
    public override DataCatalogCategory Category => DataCatalogCategory.DataRelationships;
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
        "Visual data lineage graph showing source-to-target relationships, transformations, and data flow. " +
        "Supports column-level lineage with impact and root cause analysis.";
    public override string[] Tags => ["lineage", "graph", "visualization", "impact-analysis", "column-level"];
}

/// <summary>
/// Foreign Key Relationship Strategy - Database FK relationships.
/// Implements T128.5: Data relationships.
/// </summary>
public sealed class ForeignKeyRelationshipStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "foreign-key-relationship";
    public override string DisplayName => "Foreign Key Relationships";
    public override DataCatalogCategory Category => DataCatalogCategory.DataRelationships;
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
        "Manages foreign key relationships between tables with automatic discovery from database constraints. " +
        "Generates ER diagrams and relationship documentation.";
    public override string[] Tags => ["foreign-key", "constraints", "er-diagram", "references", "database"];
}

/// <summary>
/// Inferred Relationship Strategy - ML-inferred relationships.
/// Implements T128.5: Data relationships.
/// </summary>
public sealed class InferredRelationshipStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "inferred-relationship";
    public override string DisplayName => "Inferred Relationships";
    public override DataCatalogCategory Category => DataCatalogCategory.DataRelationships;
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
        "Uses ML to infer relationships based on column names, data patterns, value distributions, and " +
        "join key analysis. Suggests potential foreign keys and data links.";
    public override string[] Tags => ["ml", "inference", "suggestions", "patterns", "join-keys"];
}

/// <summary>
/// Knowledge Graph Strategy - Enterprise knowledge graph.
/// Implements T128.5: Data relationships.
/// </summary>
public sealed class KnowledgeGraphStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "knowledge-graph";
    public override string DisplayName => "Enterprise Knowledge Graph";
    public override DataCatalogCategory Category => DataCatalogCategory.DataRelationships;
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
        "Enterprise knowledge graph connecting data assets, owners, domains, applications, and business processes. " +
        "Supports SPARQL queries and graph traversal.";
    public override string[] Tags => ["knowledge-graph", "rdf", "sparql", "ontology", "semantic-web"];
}

/// <summary>
/// Data Domain Mapping Strategy - Business domain relationships.
/// Implements T128.5: Data relationships.
/// </summary>
public sealed class DataDomainMappingStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "data-domain-mapping";
    public override string DisplayName => "Data Domain Mapping";
    public override DataCatalogCategory Category => DataCatalogCategory.DataRelationships;
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
        "Maps data assets to business domains (Customer, Product, Sales, Finance) with domain hierarchies, " +
        "cross-domain relationships, and domain-bounded contexts.";
    public override string[] Tags => ["domain", "business", "mapping", "bounded-context", "ddd"];
}

/// <summary>
/// Application Dependency Strategy - Application data dependencies.
/// Implements T128.5: Data relationships.
/// </summary>
public sealed class ApplicationDependencyStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "application-dependency";
    public override string DisplayName => "Application Dependencies";
    public override DataCatalogCategory Category => DataCatalogCategory.DataRelationships;
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
        "Tracks which applications read from and write to data assets. Maps application-to-data " +
        "dependencies for impact analysis and change management.";
    public override string[] Tags => ["applications", "dependencies", "consumers", "producers", "impact"];
}

/// <summary>
/// Pipeline Dependency Strategy - ETL/ELT pipeline relationships.
/// Implements T128.5: Data relationships.
/// </summary>
public sealed class PipelineDependencyStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "pipeline-dependency";
    public override string DisplayName => "Pipeline Dependencies";
    public override DataCatalogCategory Category => DataCatalogCategory.DataRelationships;
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
        "Tracks data pipeline dependencies including Airflow DAGs, dbt models, Spark jobs, and orchestration " +
        "workflows. Shows pipeline execution order and data freshness.";
    public override string[] Tags => ["pipeline", "etl", "dbt", "airflow", "orchestration", "freshness"];
}

/// <summary>
/// Cross-Dataset Join Strategy - Identifies joinable datasets.
/// Implements T128.5: Data relationships.
/// </summary>
public sealed class CrossDatasetJoinStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "cross-dataset-join";
    public override string DisplayName => "Cross-Dataset Joins";
    public override DataCatalogCategory Category => DataCatalogCategory.DataRelationships;
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
        "Identifies potential join keys across datasets based on column names, data types, and value overlap. " +
        "Suggests join paths and generates sample join queries.";
    public override string[] Tags => ["joins", "cross-dataset", "join-keys", "federation", "queries"];
}

/// <summary>
/// Business Process Mapping Strategy - Maps data to processes.
/// Implements T128.5: Data relationships.
/// </summary>
public sealed class BusinessProcessMappingStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "business-process-mapping";
    public override string DisplayName => "Business Process Mapping";
    public override DataCatalogCategory Category => DataCatalogCategory.DataRelationships;
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
        "Maps data assets to business processes and workflows. Shows which data supports order fulfillment, " +
        "customer onboarding, financial close, and other key processes.";
    public override string[] Tags => ["business-process", "workflow", "bpmn", "value-stream", "operations"];
}

/// <summary>
/// Data Contract Strategy - Producer-consumer contracts.
/// Implements T128.5: Data relationships.
/// </summary>
public sealed class DataContractStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "data-contract";
    public override string DisplayName => "Data Contracts";
    public override DataCatalogCategory Category => DataCatalogCategory.DataRelationships;
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
        "Manages formal data contracts between producers and consumers defining schema guarantees, SLAs, " +
        "quality expectations, and notification requirements.";
    public override string[] Tags => ["contracts", "sla", "producer", "consumer", "guarantees"];
}
