namespace DataWarehouse.SDK.Primitives.Metadata;

/// <summary>
/// Defines the scope of metadata.
/// </summary>
public enum MetadataScope
{
    /// <summary>Metadata associated with a single object (file, record, etc.).</summary>
    Object = 0,

    /// <summary>Metadata associated with a container (bucket, table, collection).</summary>
    Container = 1,

    /// <summary>Metadata associated with the entire system or warehouse.</summary>
    System = 2,

    /// <summary>Metadata associated with a user or principal.</summary>
    User = 3,

    /// <summary>Metadata associated with a tenant in multi-tenant scenarios.</summary>
    Tenant = 4
}

/// <summary>
/// Represents a single metadata entry with type information and timestamps.
/// </summary>
/// <param name="Key">The metadata key.</param>
/// <param name="Value">The metadata value.</param>
/// <param name="ValueType">The .NET type of the value.</param>
/// <param name="CreatedUtc">When this entry was first created.</param>
/// <param name="ModifiedUtc">When this entry was last modified.</param>
/// <param name="CreatedBy">Who created this entry (optional).</param>
/// <param name="Tags">Optional tags associated with this entry.</param>
public record MetadataEntry(
    string Key,
    object Value,
    Type ValueType,
    DateTime CreatedUtc,
    DateTime ModifiedUtc,
    string? CreatedBy = null,
    IReadOnlyList<string>? Tags = null);

/// <summary>
/// Represents a query for searching metadata.
/// </summary>
/// <param name="Filter">Filter expression (provider-specific syntax).</param>
/// <param name="Scope">The scope to search within.</param>
/// <param name="Keys">Specific keys to filter by (null for all keys).</param>
/// <param name="Tags">Tags to filter by (all must match if specified).</param>
/// <param name="Skip">Number of results to skip (for pagination).</param>
/// <param name="Take">Maximum number of results to return (null for unlimited).</param>
/// <param name="OrderBy">Field to order results by (optional).</param>
/// <param name="Descending">If true, order descending; otherwise ascending.</param>
public record MetadataQuery(
    string? Filter = null,
    MetadataScope Scope = MetadataScope.Object,
    IReadOnlyList<string>? Keys = null,
    IReadOnlyList<string>? Tags = null,
    int Skip = 0,
    int? Take = null,
    string? OrderBy = null,
    bool Descending = false);

/// <summary>
/// Represents the result of a metadata query.
/// </summary>
/// <param name="ObjectId">The unique identifier of the object.</param>
/// <param name="Metadata">The metadata entries that match the query.</param>
/// <param name="Scope">The scope of the metadata.</param>
/// <param name="Score">Relevance score (if applicable, e.g., for full-text search).</param>
public record MetadataQueryResult(
    string ObjectId,
    IReadOnlyDictionary<string, object> Metadata,
    MetadataScope Scope,
    double? Score = null);

/// <summary>
/// Represents a versioned snapshot of metadata.
/// </summary>
/// <param name="Version">The version number or identifier.</param>
/// <param name="Metadata">The metadata at this version.</param>
/// <param name="Timestamp">When this version was created.</param>
/// <param name="ModifiedBy">Who created this version (optional).</param>
/// <param name="ChangeDescription">Description of what changed (optional).</param>
public record MetadataVersion(
    string Version,
    IReadOnlyDictionary<string, object> Metadata,
    DateTime Timestamp,
    string? ModifiedBy = null,
    string? ChangeDescription = null);

/// <summary>
/// Describes the capabilities of a metadata provider.
/// </summary>
/// <param name="SupportsVersioning">True if the provider maintains metadata history.</param>
/// <param name="SupportsQuerying">True if the provider supports metadata queries.</param>
/// <param name="SupportsTagging">True if the provider supports tags on metadata entries.</param>
/// <param name="SupportsTransactions">True if the provider supports transactional updates.</param>
/// <param name="MaxMetadataSize">Maximum size of metadata per object in bytes (0 = unlimited).</param>
/// <param name="MaxKeyLength">Maximum length of metadata keys (0 = unlimited).</param>
/// <param name="SupportedScopes">The metadata scopes supported by this provider.</param>
/// <param name="SupportedValueTypes">The .NET types supported for metadata values.</param>
public record MetadataCapabilities(
    bool SupportsVersioning,
    bool SupportsQuerying,
    bool SupportsTagging,
    bool SupportsTransactions,
    long MaxMetadataSize,
    int MaxKeyLength,
    IReadOnlySet<MetadataScope> SupportedScopes,
    IReadOnlySet<Type> SupportedValueTypes);

/// <summary>
/// Configuration options for metadata providers.
/// </summary>
/// <param name="EnableVersioning">If true, maintain metadata version history.</param>
/// <param name="EnableIndexing">If true, create indexes for efficient querying.</param>
/// <param name="RetentionDays">Number of days to retain old metadata versions (0 = forever).</param>
/// <param name="CacheTTLSeconds">Time-to-live for metadata cache in seconds (0 = no cache).</param>
/// <param name="EnableCompression">If true, compress metadata to save space.</param>
public record MetadataConfig(
    bool EnableVersioning = false,
    bool EnableIndexing = true,
    int RetentionDays = 0,
    int CacheTTLSeconds = 300,
    bool EnableCompression = false);

/// <summary>
/// Represents metadata lineage information for tracking data provenance.
/// </summary>
/// <param name="ObjectId">The object this lineage refers to.</param>
/// <param name="SourceObjects">Objects that this object was derived from.</param>
/// <param name="DerivedObjects">Objects that were derived from this object.</param>
/// <param name="Transformations">Transformations applied to create this object.</param>
/// <param name="Timestamp">When this lineage was recorded.</param>
public record MetadataLineage(
    string ObjectId,
    IReadOnlyList<string> SourceObjects,
    IReadOnlyList<string> DerivedObjects,
    IReadOnlyList<string> Transformations,
    DateTime Timestamp);
