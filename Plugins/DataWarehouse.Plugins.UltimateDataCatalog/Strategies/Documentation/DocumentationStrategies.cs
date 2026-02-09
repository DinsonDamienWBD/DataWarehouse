namespace DataWarehouse.Plugins.UltimateDataCatalog.Strategies.Documentation;

/// <summary>
/// Business Glossary Strategy - Business term definitions and mappings.
/// Implements T128.3: Data documentation.
/// </summary>
public sealed class BusinessGlossaryStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "business-glossary";
    public override string DisplayName => "Business Glossary";
    public override DataCatalogCategory Category => DataCatalogCategory.Documentation;
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
        "Centralized business glossary for defining, managing, and linking business terms to technical assets. " +
        "Supports term hierarchies, synonyms, and cross-references.";
    public override string[] Tags => ["glossary", "business-terms", "definitions", "synonyms", "hierarchy"];
}

/// <summary>
/// Data Dictionary Strategy - Technical data element documentation.
/// Implements T128.3: Data documentation.
/// </summary>
public sealed class DataDictionaryStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "data-dictionary";
    public override string DisplayName => "Data Dictionary";
    public override DataCatalogCategory Category => DataCatalogCategory.Documentation;
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
        "Technical data dictionary documenting columns, data types, formats, constraints, and valid values. " +
        "Links to business glossary terms and provides code lists.";
    public override string[] Tags => ["dictionary", "columns", "data-types", "constraints", "code-lists"];
}

/// <summary>
/// Automated Documentation Generator Strategy - AI-powered documentation.
/// Implements T128.3: Data documentation.
/// </summary>
public sealed class AutomatedDocumentationGeneratorStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "automated-documentation-generator";
    public override string DisplayName => "Automated Documentation Generator";
    public override DataCatalogCategory Category => DataCatalogCategory.Documentation;
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
        "Uses AI/LLM to automatically generate documentation from data samples, schemas, and patterns. " +
        "Suggests descriptions, identifies PII, and classifies data sensitivity.";
    public override string[] Tags => ["ai", "llm", "automation", "generation", "pii-detection"];
}

/// <summary>
/// Data Quality Documentation Strategy - Quality metrics documentation.
/// Implements T128.3: Data documentation.
/// </summary>
public sealed class DataQualityDocumentationStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "data-quality-documentation";
    public override string DisplayName => "Data Quality Documentation";
    public override DataCatalogCategory Category => DataCatalogCategory.Documentation;
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
        "Documents data quality metrics, SLAs, and expectations including completeness, accuracy, timeliness, " +
        "validity, and consistency measures with historical tracking.";
    public override string[] Tags => ["quality", "metrics", "sla", "completeness", "accuracy", "timeliness"];
}

/// <summary>
/// Usage Documentation Strategy - Documents data usage patterns.
/// Implements T128.3: Data documentation.
/// </summary>
public sealed class UsageDocumentationStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "usage-documentation";
    public override string DisplayName => "Usage Documentation";
    public override DataCatalogCategory Category => DataCatalogCategory.Documentation;
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
        "Documents data usage patterns including who accesses data, how frequently, for what purposes, " +
        "and sample queries. Helps users understand practical applications.";
    public override string[] Tags => ["usage", "access-patterns", "queries", "consumers", "applications"];
}

/// <summary>
/// Data Ownership Documentation Strategy - Documents data ownership.
/// Implements T128.3: Data documentation.
/// </summary>
public sealed class DataOwnershipDocumentationStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "data-ownership-documentation";
    public override string DisplayName => "Data Ownership Documentation";
    public override DataCatalogCategory Category => DataCatalogCategory.Documentation;
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
        "Documents data ownership including data stewards, domain owners, technical owners, and approval workflows. " +
        "Supports RACI matrices and escalation paths.";
    public override string[] Tags => ["ownership", "stewardship", "raci", "accountability", "governance"];
}

/// <summary>
/// Collaborative Annotation Strategy - Crowdsourced documentation.
/// Implements T128.3: Data documentation.
/// </summary>
public sealed class CollaborativeAnnotationStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "collaborative-annotation";
    public override string DisplayName => "Collaborative Annotation";
    public override DataCatalogCategory Category => DataCatalogCategory.Documentation;
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
        "Enables collaborative documentation with comments, annotations, ratings, and wiki-style editing. " +
        "Supports review workflows and change tracking.";
    public override string[] Tags => ["collaboration", "annotations", "wiki", "comments", "reviews"];
}

/// <summary>
/// Data Classification Strategy - Sensitivity and compliance classification.
/// Implements T128.3: Data documentation.
/// </summary>
public sealed class DataClassificationStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "data-classification";
    public override string DisplayName => "Data Classification";
    public override DataCatalogCategory Category => DataCatalogCategory.Documentation;
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
        "Classifies data by sensitivity (public, internal, confidential, restricted), compliance requirements " +
        "(GDPR, HIPAA, PCI), and business criticality with automated classification rules.";
    public override string[] Tags => ["classification", "sensitivity", "gdpr", "hipaa", "pci", "compliance"];
}

/// <summary>
/// Documentation Template Strategy - Standardized documentation templates.
/// Implements T128.3: Data documentation.
/// </summary>
public sealed class DocumentationTemplateStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "documentation-template";
    public override string DisplayName => "Documentation Templates";
    public override DataCatalogCategory Category => DataCatalogCategory.Documentation;
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
        "Provides standardized documentation templates for different asset types ensuring consistent, " +
        "complete documentation across the organization with required and optional fields.";
    public override string[] Tags => ["templates", "standards", "consistency", "required-fields", "best-practices"];
}

/// <summary>
/// Documentation Export Strategy - Exports documentation in various formats.
/// Implements T128.3: Data documentation.
/// </summary>
public sealed class DocumentationExportStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "documentation-export";
    public override string DisplayName => "Documentation Export";
    public override DataCatalogCategory Category => DataCatalogCategory.Documentation;
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
        "Exports catalog documentation to various formats including Markdown, HTML, PDF, Confluence, " +
        "and SharePoint for offline access and external sharing.";
    public override string[] Tags => ["export", "markdown", "html", "pdf", "confluence", "sharepoint"];
}
