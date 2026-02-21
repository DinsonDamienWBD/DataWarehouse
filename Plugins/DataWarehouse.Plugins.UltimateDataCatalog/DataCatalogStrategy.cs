using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.UltimateDataCatalog;

/// <summary>
/// Defines the category of data catalog strategy.
/// </summary>
public enum DataCatalogCategory
{
    /// <summary>Asset discovery strategies (128.1).</summary>
    AssetDiscovery,
    /// <summary>Schema registry strategies (128.2).</summary>
    SchemaRegistry,
    /// <summary>Data documentation strategies (128.3).</summary>
    Documentation,
    /// <summary>Search and discovery strategies (128.4).</summary>
    SearchDiscovery,
    /// <summary>Data relationships strategies (128.5).</summary>
    DataRelationships,
    /// <summary>Access control integration strategies (128.6).</summary>
    AccessControl,
    /// <summary>Catalog API strategies (128.7).</summary>
    CatalogApi,
    /// <summary>Catalog UI component strategies (128.8).</summary>
    CatalogUI
}

/// <summary>
/// Capabilities for data catalog strategies.
/// </summary>
public sealed record DataCatalogCapabilities
{
    /// <summary>Whether the strategy supports async operations.</summary>
    public required bool SupportsAsync { get; init; }
    /// <summary>Whether the strategy supports batch operations.</summary>
    public required bool SupportsBatch { get; init; }
    /// <summary>Whether the strategy supports real-time updates.</summary>
    public required bool SupportsRealTime { get; init; }
    /// <summary>Whether the strategy supports federation across sources.</summary>
    public required bool SupportsFederation { get; init; }
    /// <summary>Whether the strategy supports versioning.</summary>
    public required bool SupportsVersioning { get; init; }
    /// <summary>Whether the strategy supports multi-tenancy.</summary>
    public required bool SupportsMultiTenancy { get; init; }
    /// <summary>Maximum catalog entries (0 = unlimited).</summary>
    public long MaxEntries { get; init; }
}

/// <summary>
/// Interface for data catalog strategies.
/// </summary>
public interface IDataCatalogStrategy
{
    /// <summary>Unique identifier for this strategy.</summary>
    string StrategyId { get; }
    /// <summary>Human-readable display name.</summary>
    string DisplayName { get; }
    /// <summary>Category of this strategy.</summary>
    DataCatalogCategory Category { get; }
    /// <summary>Capabilities of this strategy.</summary>
    DataCatalogCapabilities Capabilities { get; }
    /// <summary>Semantic description for AI discovery.</summary>
    string SemanticDescription { get; }
    /// <summary>Tags for categorization and discovery.</summary>
    string[] Tags { get; }
}

/// <summary>
/// Abstract base class for data catalog strategies.
/// Inherits lifecycle management, counters, health checks, and dispose from StrategyBase.
/// </summary>
public abstract class DataCatalogStrategyBase : StrategyBase, IDataCatalogStrategy
{
    /// <inheritdoc/>
    public abstract override string StrategyId { get; }
    /// <inheritdoc/>
    public abstract string DisplayName { get; }
    /// <inheritdoc/>
    public override string Name => DisplayName;
    /// <inheritdoc/>
    public abstract DataCatalogCategory Category { get; }
    /// <inheritdoc/>
    public abstract DataCatalogCapabilities Capabilities { get; }
    /// <inheritdoc/>
    public abstract string SemanticDescription { get; }
    /// <inheritdoc/>
    public abstract string[] Tags { get; }
}
