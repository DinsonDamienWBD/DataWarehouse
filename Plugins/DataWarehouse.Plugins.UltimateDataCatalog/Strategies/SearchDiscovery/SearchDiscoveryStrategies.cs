namespace DataWarehouse.Plugins.UltimateDataCatalog.Strategies.SearchDiscovery;

/// <summary>
/// Full-Text Search Strategy - Comprehensive text-based search.
/// Implements T128.4: Search and discovery.
/// </summary>
public sealed class FullTextSearchStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "full-text-search";
    public override string DisplayName => "Full-Text Search";
    public override DataCatalogCategory Category => DataCatalogCategory.SearchDiscovery;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsFederation = true,
        SupportsVersioning = false,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Full-text search across all catalog metadata including names, descriptions, schemas, and tags. " +
        "Supports fuzzy matching, wildcards, phrase search, and relevance ranking.";
    public override string[] Tags => ["search", "full-text", "fuzzy", "wildcards", "relevance"];
}

/// <summary>
/// Semantic Search Strategy - AI-powered semantic understanding.
/// Implements T128.4: Search and discovery.
/// </summary>
public sealed class SemanticSearchStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "semantic-search";
    public override string DisplayName => "Semantic Search";
    public override DataCatalogCategory Category => DataCatalogCategory.SearchDiscovery;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsRealTime = true,
        SupportsFederation = true,
        SupportsVersioning = false,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "AI-powered semantic search using embeddings and vector similarity. Understands natural language queries, " +
        "synonyms, context, and intent to find relevant data assets.";
    public override string[] Tags => ["semantic", "ai", "embeddings", "nlp", "vector-search"];
}

/// <summary>
/// Faceted Search Strategy - Filterable multi-dimensional search.
/// Implements T128.4: Search and discovery.
/// </summary>
public sealed class FacetedSearchStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "faceted-search";
    public override string DisplayName => "Faceted Search";
    public override DataCatalogCategory Category => DataCatalogCategory.SearchDiscovery;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsFederation = true,
        SupportsVersioning = false,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Faceted navigation with dynamic filter aggregations by domain, type, owner, tag, quality score, " +
        "and freshness. Enables drill-down exploration with count summaries.";
    public override string[] Tags => ["faceted", "filters", "aggregations", "drill-down", "navigation"];
}

/// <summary>
/// Column Search Strategy - Search by column name and type.
/// Implements T128.4: Search and discovery.
/// </summary>
public sealed class ColumnSearchStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "column-search";
    public override string DisplayName => "Column Search";
    public override DataCatalogCategory Category => DataCatalogCategory.SearchDiscovery;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsFederation = true,
        SupportsVersioning = false,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Search for tables containing specific columns by name, data type, or pattern. " +
        "Find assets with customer_id, email columns, or PII-related fields.";
    public override string[] Tags => ["column", "fields", "data-type", "pattern", "pii"];
}

/// <summary>
/// Tag-Based Discovery Strategy - Discovery through tagging.
/// Implements T128.4: Search and discovery.
/// </summary>
public sealed class TagBasedDiscoveryStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "tag-based-discovery";
    public override string DisplayName => "Tag-Based Discovery";
    public override DataCatalogCategory Category => DataCatalogCategory.SearchDiscovery;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsFederation = true,
        SupportsVersioning = false,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Discovery through hierarchical tags and taxonomies. Browse by domain, classification, " +
        "compliance label, or custom tag hierarchies with tag suggestions.";
    public override string[] Tags => ["tags", "taxonomy", "classification", "labels", "hierarchical"];
}

/// <summary>
/// Similarity-Based Discovery Strategy - Find similar assets.
/// Implements T128.4: Search and discovery.
/// </summary>
public sealed class SimilarityBasedDiscoveryStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "similarity-based-discovery";
    public override string DisplayName => "Similarity-Based Discovery";
    public override DataCatalogCategory Category => DataCatalogCategory.SearchDiscovery;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsFederation = true,
        SupportsVersioning = false,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Finds similar data assets based on schema structure, column overlap, data patterns, " +
        "and usage patterns. Identifies duplicate or near-duplicate datasets.";
    public override string[] Tags => ["similarity", "duplicates", "overlap", "patterns", "matching"];
}

/// <summary>
/// Recommendation Engine Strategy - AI-powered recommendations.
/// Implements T128.4: Search and discovery.
/// </summary>
public sealed class RecommendationEngineStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "recommendation-engine";
    public override string DisplayName => "Recommendation Engine";
    public override DataCatalogCategory Category => DataCatalogCategory.SearchDiscovery;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsRealTime = true,
        SupportsFederation = true,
        SupportsVersioning = false,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Personalized recommendations based on user role, past searches, access patterns, and team behavior. " +
        "Suggests relevant datasets, popular assets, and related resources.";
    public override string[] Tags => ["recommendations", "personalized", "ai", "suggestions", "popular"];
}

/// <summary>
/// Lineage-Based Discovery Strategy - Discover through lineage.
/// Implements T128.4: Search and discovery.
/// </summary>
public sealed class LineageBasedDiscoveryStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "lineage-based-discovery";
    public override string DisplayName => "Lineage-Based Discovery";
    public override DataCatalogCategory Category => DataCatalogCategory.SearchDiscovery;
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
        "Discover data by exploring lineage graphs - find upstream sources, downstream consumers, " +
        "and related transformations with visual graph navigation.";
    public override string[] Tags => ["lineage", "upstream", "downstream", "graph", "relationships"];
}

/// <summary>
/// Natural Language Query Strategy - Query with natural language.
/// Implements T128.4: Search and discovery.
/// </summary>
public sealed class NaturalLanguageQueryStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "natural-language-query";
    public override string DisplayName => "Natural Language Query";
    public override DataCatalogCategory Category => DataCatalogCategory.SearchDiscovery;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsRealTime = true,
        SupportsFederation = true,
        SupportsVersioning = false,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };
    public override string SemanticDescription =>
        "Ask questions in plain English like 'find customer tables with email addresses' or " +
        "'show me sales data from last quarter' and get relevant results.";
    public override string[] Tags => ["nlp", "natural-language", "conversational", "questions", "plain-english"];
}

/// <summary>
/// Saved Search Strategy - Persistent search queries.
/// Implements T128.4: Search and discovery.
/// </summary>
public sealed class SavedSearchStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "saved-search";
    public override string DisplayName => "Saved Searches";
    public override DataCatalogCategory Category => DataCatalogCategory.SearchDiscovery;
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
        "Save, share, and subscribe to search queries. Get notifications when new assets match saved criteria. " +
        "Create team-shared searches and watchlists.";
    public override string[] Tags => ["saved", "subscriptions", "notifications", "watchlists", "sharing"];
}
