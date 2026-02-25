using DataWarehouse.SDK.Contracts.DataMesh;

namespace DataWarehouse.Plugins.UltimateDataMesh.Strategies.DomainDiscovery;

/// <summary>
/// Data Catalog Discovery Strategy - Searchable data catalog.
/// Implements T113.5: Domain data discovery.
/// </summary>
public sealed class DataCatalogDiscoveryStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "data-catalog-discovery";
    public override string DisplayName => "Data Catalog Discovery";
    public override DataMeshCategory Category => DataMeshCategory.DomainDiscovery;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = true,
        SupportsEventDriven = true,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Unified data catalog with full-text search, faceted filtering, and semantic discovery. " +
        "Enables finding data products across domains with rich metadata and previews.";
    public override string[] Tags => ["catalog", "search", "discovery", "metadata", "preview"];
}

/// <summary>
/// Semantic Search Strategy - AI-powered semantic data discovery.
/// Implements T113.5: Domain data discovery.
/// </summary>
public sealed class SemanticSearchStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "semantic-search";
    public override string DisplayName => "Semantic Search";
    public override DataMeshCategory Category => DataMeshCategory.DomainDiscovery;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = false,
        SupportsEventDriven = false,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "AI-powered semantic search using embeddings and natural language queries. " +
        "Understands synonyms, relationships, and context for intelligent data discovery.";
    public override string[] Tags => ["semantic", "ai", "nlp", "embeddings", "intelligent-search"];
}

/// <summary>
/// Data Lineage Discovery Strategy - Discover data through lineage.
/// Implements T113.5: Domain data discovery.
/// </summary>
public sealed class DataLineageDiscoveryStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "data-lineage-discovery";
    public override string DisplayName => "Lineage Discovery";
    public override DataMeshCategory Category => DataMeshCategory.DomainDiscovery;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = true,
        SupportsEventDriven = true,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Data discovery through lineage exploration - find related data products by tracing " +
        "upstream sources and downstream consumers with visual lineage graphs.";
    public override string[] Tags => ["lineage", "upstream", "downstream", "graph", "relationships"];
}

/// <summary>
/// Domain Marketplace Strategy - Data product marketplace.
/// Implements T113.5: Domain data discovery.
/// </summary>
public sealed class DomainMarketplaceStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "domain-marketplace";
    public override string DisplayName => "Domain Marketplace";
    public override DataMeshCategory Category => DataMeshCategory.DomainDiscovery;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = false,
        SupportsEventDriven = true,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Internal data marketplace for browsing, rating, and subscribing to data products. " +
        "Features product categories, recommendations, and usage analytics.";
    public override string[] Tags => ["marketplace", "browse", "ratings", "recommendations", "subscription"];
}

/// <summary>
/// Knowledge Graph Strategy - Graph-based data discovery.
/// Implements T113.5: Domain data discovery.
/// </summary>
public sealed class KnowledgeGraphStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "knowledge-graph";
    public override string DisplayName => "Knowledge Graph";
    public override DataMeshCategory Category => DataMeshCategory.DomainDiscovery;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = true,
        SupportsEventDriven = true,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Enterprise knowledge graph connecting data products, domains, owners, and consumers. " +
        "Enables graph traversal queries and relationship-based discovery.";
    public override string[] Tags => ["knowledge-graph", "relationships", "traversal", "ontology", "rdf"];
}

/// <summary>
/// Auto-Discovery Strategy - Automated data asset discovery.
/// Implements T113.5: Domain data discovery.
/// </summary>
public sealed class AutoDiscoveryStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "auto-discovery";
    public override string DisplayName => "Auto-Discovery";
    public override DataMeshCategory Category => DataMeshCategory.DomainDiscovery;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = false,
        SupportsBatch = true,
        SupportsEventDriven = true,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Automated discovery of data assets using crawlers, schema inference, and metadata extraction. " +
        "Continuously scans for new data sources and suggests catalog entries.";
    public override string[] Tags => ["auto-discovery", "crawlers", "inference", "extraction", "automation"];
}

/// <summary>
/// Data Profiling Discovery Strategy - Discover data through profiling.
/// Implements T113.5: Domain data discovery.
/// </summary>
public sealed class DataProfilingDiscoveryStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "data-profiling-discovery";
    public override string DisplayName => "Profiling Discovery";
    public override DataMeshCategory Category => DataMeshCategory.DomainDiscovery;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = false,
        SupportsBatch = true,
        SupportsEventDriven = false,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Data discovery through automated profiling - understand data distribution, patterns, " +
        "quality metrics, and statistical summaries to find relevant data products.";
    public override string[] Tags => ["profiling", "statistics", "patterns", "distribution", "quality"];
}
