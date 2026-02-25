namespace DataWarehouse.Plugins.UltimateDataCatalog.Strategies.Marketplace;

/// <summary>
/// Data Marketplace Strategy - Commerce and monetization for data assets.
/// Merged from DataMarketplace plugin (T83).
///
/// Provides data commerce capabilities:
/// - T83.2: Subscription engine (time-based and query-based access)
/// - T83.3: Usage metering (queries, bytes, compute)
/// - T83.4: Billing integration (invoices, payments)
/// - T83.5: License management (usage term enforcement)
/// - T83.6: Access revocation (automatic on payment failure)
/// - T83.9: Chargeback reporting (internal cost allocation)
/// - T83.10: Smart contract integration (blockchain-based)
/// </summary>
public sealed class DataMarketplaceStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "data-marketplace";
    public override string DisplayName => "Data Marketplace";
    public override DataCatalogCategory Category => DataCatalogCategory.AccessControl;
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
        "Data marketplace strategy providing commerce and monetization for data assets. " +
        "Supports subscription management, usage metering, billing integration, license enforcement, " +
        "access revocation, chargeback reporting, and smart contract integration.";
    public override string[] Tags => ["marketplace", "commerce", "subscription", "billing", "license", "metering", "smart-contract"];
}

/// <summary>
/// Data Listing Strategy - Publishing and discovery of data products.
/// Merged from DataMarketplace plugin (T83).
///
/// Provides data listing capabilities:
/// - T83.1: Data listing (publish datasets with pricing and terms)
/// - T83.7: Data preview (sample data without full access)
/// - T83.8: Rating and reviews (buyer feedback on quality)
/// </summary>
public sealed class DataListingStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "data-listing";
    public override string DisplayName => "Data Listing";
    public override DataCatalogCategory Category => DataCatalogCategory.AssetDiscovery;
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
        "Data listing strategy for publishing and discovering data products in the marketplace. " +
        "Supports dataset publication with pricing and terms, data preview without full access, " +
        "and rating/review system for buyer feedback on data quality.";
    public override string[] Tags => ["listing", "data-product", "preview", "rating", "review", "pricing", "publication"];
}
