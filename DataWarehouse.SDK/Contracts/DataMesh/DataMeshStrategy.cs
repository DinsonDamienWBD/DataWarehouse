using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.DataMesh;

/// <summary>
/// Defines the category of data mesh strategy.
/// </summary>
public enum DataMeshCategory
{
    /// <summary>Domain-oriented data ownership strategies (T113.1).</summary>
    DomainOwnership,
    /// <summary>Data as a product strategies (T113.2).</summary>
    DataProduct,
    /// <summary>Self-serve data infrastructure strategies (T113.3).</summary>
    SelfServe,
    /// <summary>Federated computational governance strategies (T113.4).</summary>
    FederatedGovernance,
    /// <summary>Domain data discovery strategies (T113.5).</summary>
    DomainDiscovery,
    /// <summary>Cross-domain data sharing strategies (T113.6).</summary>
    CrossDomainSharing,
    /// <summary>Data mesh observability strategies (T113.7).</summary>
    MeshObservability,
    /// <summary>Data mesh security strategies (T113.8).</summary>
    MeshSecurity
}

/// <summary>
/// Represents the capabilities of a data mesh strategy.
/// </summary>
public sealed record DataMeshCapabilities
{
    /// <summary>Whether the strategy supports async operations.</summary>
    public required bool SupportsAsync { get; init; }
    /// <summary>Whether the strategy supports real-time operations.</summary>
    public required bool SupportsRealTime { get; init; }
    /// <summary>Whether the strategy supports batch operations.</summary>
    public required bool SupportsBatch { get; init; }
    /// <summary>Whether the strategy supports event-driven operations.</summary>
    public required bool SupportsEventDriven { get; init; }
    /// <summary>Whether the strategy supports multi-tenancy.</summary>
    public required bool SupportsMultiTenancy { get; init; }
    /// <summary>Whether the strategy supports federation.</summary>
    public required bool SupportsFederation { get; init; }
    /// <summary>Maximum domains supported (0 = unlimited).</summary>
    public int MaxDomains { get; init; }
    /// <summary>Maximum data products supported (0 = unlimited).</summary>
    public int MaxDataProducts { get; init; }
}

/// <summary>
/// Represents data mesh statistics.
/// </summary>
public sealed class DataMeshStatistics
{
    /// <summary>Total domains registered.</summary>
    public long TotalDomains { get; set; }
    /// <summary>Total data products.</summary>
    public long TotalDataProducts { get; set; }
    /// <summary>Total data consumers.</summary>
    public long TotalConsumers { get; set; }
    /// <summary>Total cross-domain shares.</summary>
    public long TotalCrossDomainShares { get; set; }
    /// <summary>Total governance policies.</summary>
    public long TotalGovernancePolicies { get; set; }
    /// <summary>Total API calls.</summary>
    public long TotalApiCalls { get; set; }
    /// <summary>Total data requests.</summary>
    public long TotalDataRequests { get; set; }
    /// <summary>Domains by status.</summary>
    public Dictionary<DomainStatus, long> DomainsByStatus { get; set; } = new();
    /// <summary>Data products by quality tier.</summary>
    public Dictionary<DataProductQualityTier, long> ProductsByQualityTier { get; set; } = new();
}

/// <summary>
/// Interface for data mesh strategies.
/// </summary>
public interface IDataMeshStrategy
{
    /// <summary>Unique identifier for this strategy.</summary>
    string StrategyId { get; }
    /// <summary>Human-readable display name.</summary>
    string DisplayName { get; }
    /// <summary>Category of this strategy.</summary>
    DataMeshCategory Category { get; }
    /// <summary>Capabilities of this strategy.</summary>
    DataMeshCapabilities Capabilities { get; }
    /// <summary>Semantic description for AI discovery.</summary>
    string SemanticDescription { get; }
    /// <summary>Tags for categorization and discovery.</summary>
    string[] Tags { get; }
    /// <summary>Gets statistics for this strategy.</summary>
    DataMeshStatistics GetStatistics();
    /// <summary>Resets the statistics.</summary>
    void ResetStatistics();
    /// <summary>Initializes the strategy.</summary>
    Task InitializeAsync(CancellationToken ct = default);
    /// <summary>Disposes of the strategy resources.</summary>
    Task DisposeAsync();
}

/// <summary>
/// Abstract base class for data mesh strategies.
/// </summary>
public abstract class DataMeshStrategyBase : StrategyBase, IDataMeshStrategy
{
    private readonly DataMeshStatistics _statistics = new();
    private readonly object _statsLock = new();
    private new bool _initialized;

    /// <inheritdoc/>
    public override abstract string StrategyId { get; }
    /// <inheritdoc/>
    public abstract string DisplayName { get; }
    /// <inheritdoc/>
    public abstract DataMeshCategory Category { get; }
    /// <inheritdoc/>
    public abstract DataMeshCapabilities Capabilities { get; }
    /// <inheritdoc/>
    public abstract string SemanticDescription { get; }
    /// <inheritdoc/>
    public abstract string[] Tags { get; }

    /// <summary>Gets whether the strategy has been initialized.</summary>
    protected new bool IsInitialized => _initialized;

    /// <inheritdoc/>
    public DataMeshStatistics GetStatistics()
    {
        lock (_statsLock)
        {
            return new DataMeshStatistics
            {
                TotalDomains = _statistics.TotalDomains,
                TotalDataProducts = _statistics.TotalDataProducts,
                TotalConsumers = _statistics.TotalConsumers,
                TotalCrossDomainShares = _statistics.TotalCrossDomainShares,
                TotalGovernancePolicies = _statistics.TotalGovernancePolicies,
                TotalApiCalls = _statistics.TotalApiCalls,
                TotalDataRequests = _statistics.TotalDataRequests,
                DomainsByStatus = new Dictionary<DomainStatus, long>(_statistics.DomainsByStatus),
                ProductsByQualityTier = new Dictionary<DataProductQualityTier, long>(_statistics.ProductsByQualityTier)
            };
        }
    }

    /// <inheritdoc/>
    public void ResetStatistics()
    {
        lock (_statsLock)
        {
            _statistics.TotalDomains = 0;
            _statistics.TotalDataProducts = 0;
            _statistics.TotalConsumers = 0;
            _statistics.TotalCrossDomainShares = 0;
            _statistics.TotalGovernancePolicies = 0;
            _statistics.TotalApiCalls = 0;
            _statistics.TotalDataRequests = 0;
            _statistics.DomainsByStatus.Clear();
            _statistics.ProductsByQualityTier.Clear();
        }
    }

    /// <inheritdoc/>
    public new virtual async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await InitializeCoreAsync(ct);
        _initialized = true;
    }

    /// <inheritdoc/>
    public new async Task DisposeAsync()
    {
        if (!_initialized) return;
        await DisposeCoreAsync();
        _initialized = false;
    }

    /// <summary>Core initialization logic.</summary>
    protected virtual Task InitializeCoreAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Core disposal logic.</summary>
    protected virtual Task DisposeCoreAsync() => Task.CompletedTask;

    /// <summary>Records a domain registration.</summary>
    protected void RecordDomain(DomainStatus status = DomainStatus.Active)
    {
        lock (_statsLock)
        {
            _statistics.TotalDomains++;
            _statistics.DomainsByStatus.TryGetValue(status, out var current);
            _statistics.DomainsByStatus[status] = current + 1;
        }
    }

    /// <summary>Records a data product.</summary>
    protected void RecordDataProduct(DataProductQualityTier tier = DataProductQualityTier.Standard)
    {
        lock (_statsLock)
        {
            _statistics.TotalDataProducts++;
            _statistics.ProductsByQualityTier.TryGetValue(tier, out var current);
            _statistics.ProductsByQualityTier[tier] = current + 1;
        }
    }

    /// <summary>Records a consumer registration.</summary>
    protected void RecordConsumer()
    {
        lock (_statsLock) { _statistics.TotalConsumers++; }
    }

    /// <summary>Records a cross-domain share.</summary>
    protected void RecordCrossDomainShare()
    {
        lock (_statsLock) { _statistics.TotalCrossDomainShares++; }
    }

    /// <summary>Records a governance policy.</summary>
    protected void RecordGovernancePolicy()
    {
        lock (_statsLock) { _statistics.TotalGovernancePolicies++; }
    }

    /// <summary>Records an API call.</summary>
    protected void RecordApiCall()
    {
        lock (_statsLock) { _statistics.TotalApiCalls++; }
    }

    /// <summary>Records a data request.</summary>
    protected void RecordDataRequest()
    {
        lock (_statsLock) { _statistics.TotalDataRequests++; }
    }

    /// <summary>Throws if the strategy has not been initialized.</summary>
    protected void ThrowIfNotInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException($"Strategy '{StrategyId}' has not been initialized.");
    }
}

#region Domain and Data Product Types

/// <summary>
/// Domain status in the data mesh.
/// </summary>
public enum DomainStatus
{
    /// <summary>Domain is active and operational.</summary>
    Active,
    /// <summary>Domain is being provisioned.</summary>
    Provisioning,
    /// <summary>Domain is suspended.</summary>
    Suspended,
    /// <summary>Domain is being decommissioned.</summary>
    Decommissioning,
    /// <summary>Domain is archived.</summary>
    Archived
}

/// <summary>
/// Data product quality tier.
/// </summary>
public enum DataProductQualityTier
{
    /// <summary>Bronze tier - raw data with minimal quality.</summary>
    Bronze,
    /// <summary>Silver tier - cleansed and validated data.</summary>
    Silver,
    /// <summary>Gold tier - business-ready, high-quality data.</summary>
    Gold,
    /// <summary>Platinum tier - enterprise-grade, SLA-backed data.</summary>
    Platinum,
    /// <summary>Standard tier - default quality level.</summary>
    Standard
}

/// <summary>
/// Represents a data domain in the mesh.
/// </summary>
public sealed record DataDomain
{
    /// <summary>Unique domain identifier.</summary>
    public required string DomainId { get; init; }
    /// <summary>Domain name.</summary>
    public required string Name { get; init; }
    /// <summary>Domain description.</summary>
    public string? Description { get; init; }
    /// <summary>Domain owner.</summary>
    public required string Owner { get; init; }
    /// <summary>Domain team.</summary>
    public string[] TeamMembers { get; init; } = [];
    /// <summary>Domain status.</summary>
    public DomainStatus Status { get; init; } = DomainStatus.Active;
    /// <summary>Domain classification.</summary>
    public string? Classification { get; init; }
    /// <summary>Creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>Last modified timestamp.</summary>
    public DateTimeOffset ModifiedAt { get; init; }
    /// <summary>Domain metadata.</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
    /// <summary>Domain tags.</summary>
    public string[] Tags { get; init; } = [];
}

/// <summary>
/// Represents a data product in the mesh.
/// </summary>
public sealed record DataProduct
{
    /// <summary>Unique product identifier.</summary>
    public required string ProductId { get; init; }
    /// <summary>Product name.</summary>
    public required string Name { get; init; }
    /// <summary>Product description.</summary>
    public string? Description { get; init; }
    /// <summary>Owning domain ID.</summary>
    public required string DomainId { get; init; }
    /// <summary>Product owner.</summary>
    public required string Owner { get; init; }
    /// <summary>Quality tier.</summary>
    public DataProductQualityTier QualityTier { get; init; } = DataProductQualityTier.Standard;
    /// <summary>SLA definition.</summary>
    public DataProductSla? Sla { get; init; }
    /// <summary>Schema definition.</summary>
    public DataProductSchema? Schema { get; init; }
    /// <summary>Access endpoints.</summary>
    public DataProductEndpoint[] Endpoints { get; init; } = [];
    /// <summary>Data freshness requirements.</summary>
    public DataFreshness? Freshness { get; init; }
    /// <summary>Creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>Last modified timestamp.</summary>
    public DateTimeOffset ModifiedAt { get; init; }
    /// <summary>Product metadata.</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
    /// <summary>Product tags.</summary>
    public string[] Tags { get; init; } = [];
    /// <summary>Whether the product is discoverable.</summary>
    public bool IsDiscoverable { get; init; } = true;
    /// <summary>Whether the product is deprecated.</summary>
    public bool IsDeprecated { get; init; }
    /// <summary>Deprecation notice.</summary>
    public string? DeprecationNotice { get; init; }
}

/// <summary>
/// Represents a data product SLA.
/// </summary>
public sealed record DataProductSla
{
    /// <summary>Availability target (0-1, e.g., 0.999 = 99.9%).</summary>
    public double Availability { get; init; } = 0.99;
    /// <summary>Maximum latency in milliseconds.</summary>
    public int MaxLatencyMs { get; init; } = 1000;
    /// <summary>Maximum throughput in requests per second.</summary>
    public int MaxThroughput { get; init; } = 1000;
    /// <summary>Data freshness guarantee in seconds.</summary>
    public int FreshnessSeconds { get; init; } = 3600;
    /// <summary>Support tier.</summary>
    public string SupportTier { get; init; } = "Standard";
}

/// <summary>
/// Represents a data product schema.
/// </summary>
public sealed record DataProductSchema
{
    /// <summary>Schema version.</summary>
    public int Version { get; init; } = 1;
    /// <summary>Schema format (e.g., JSON Schema, Avro, Protobuf).</summary>
    public string Format { get; init; } = "json-schema";
    /// <summary>Schema definition.</summary>
    public string? Definition { get; init; }
    /// <summary>Schema fields.</summary>
    public DataProductField[] Fields { get; init; } = [];
}

/// <summary>
/// Represents a data product field.
/// </summary>
public sealed record DataProductField
{
    /// <summary>Field name.</summary>
    public required string Name { get; init; }
    /// <summary>Field data type.</summary>
    public required string DataType { get; init; }
    /// <summary>Field description.</summary>
    public string? Description { get; init; }
    /// <summary>Whether the field is required.</summary>
    public bool Required { get; init; }
    /// <summary>Whether the field contains PII.</summary>
    public bool IsPii { get; init; }
    /// <summary>Data classification.</summary>
    public string? Classification { get; init; }
}

/// <summary>
/// Represents a data product endpoint.
/// </summary>
public sealed record DataProductEndpoint
{
    /// <summary>Endpoint type (e.g., REST, GraphQL, SQL, Streaming).</summary>
    public required string Type { get; init; }
    /// <summary>Endpoint URL or connection string.</summary>
    public required string Url { get; init; }
    /// <summary>Authentication method.</summary>
    public string AuthMethod { get; init; } = "oauth2";
    /// <summary>Endpoint documentation URL.</summary>
    public string? DocumentationUrl { get; init; }
}

/// <summary>
/// Represents data freshness requirements.
/// </summary>
public sealed record DataFreshness
{
    /// <summary>Maximum age in seconds.</summary>
    public int MaxAgeSeconds { get; init; } = 3600;
    /// <summary>Update frequency.</summary>
    public string UpdateFrequency { get; init; } = "hourly";
    /// <summary>Last updated timestamp.</summary>
    public DateTimeOffset? LastUpdated { get; init; }
}

/// <summary>
/// Represents a data consumer.
/// </summary>
public sealed record DataConsumer
{
    /// <summary>Consumer identifier.</summary>
    public required string ConsumerId { get; init; }
    /// <summary>Consumer name.</summary>
    public required string Name { get; init; }
    /// <summary>Consumer domain ID.</summary>
    public required string DomainId { get; init; }
    /// <summary>Consumer contact.</summary>
    public string? Contact { get; init; }
    /// <summary>Subscribed product IDs.</summary>
    public string[] SubscribedProducts { get; init; } = [];
    /// <summary>Registration timestamp.</summary>
    public DateTimeOffset RegisteredAt { get; init; }
}

/// <summary>
/// Represents a cross-domain share agreement.
/// </summary>
public sealed record CrossDomainShare
{
    /// <summary>Share identifier.</summary>
    public required string ShareId { get; init; }
    /// <summary>Source domain ID.</summary>
    public required string SourceDomainId { get; init; }
    /// <summary>Target domain ID.</summary>
    public required string TargetDomainId { get; init; }
    /// <summary>Data product ID.</summary>
    public required string ProductId { get; init; }
    /// <summary>Share status.</summary>
    public ShareStatus Status { get; init; } = ShareStatus.Pending;
    /// <summary>Approval timestamp.</summary>
    public DateTimeOffset? ApprovedAt { get; init; }
    /// <summary>Expiration timestamp.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
    /// <summary>Access restrictions.</summary>
    public AccessRestriction[] Restrictions { get; init; } = [];
}

/// <summary>
/// Share status.
/// </summary>
public enum ShareStatus
{
    /// <summary>Share is pending approval.</summary>
    Pending,
    /// <summary>Share is approved and active.</summary>
    Approved,
    /// <summary>Share is rejected.</summary>
    Rejected,
    /// <summary>Share is expired.</summary>
    Expired,
    /// <summary>Share is revoked.</summary>
    Revoked
}

/// <summary>
/// Represents an access restriction.
/// </summary>
public sealed record AccessRestriction
{
    /// <summary>Restriction type.</summary>
    public required string Type { get; init; }
    /// <summary>Restriction value.</summary>
    public required string Value { get; init; }
}

/// <summary>
/// Represents a governance policy.
/// </summary>
public sealed record GovernancePolicy
{
    /// <summary>Policy identifier.</summary>
    public required string PolicyId { get; init; }
    /// <summary>Policy name.</summary>
    public required string Name { get; init; }
    /// <summary>Policy description.</summary>
    public string? Description { get; init; }
    /// <summary>Policy type.</summary>
    public required GovernancePolicyType Type { get; init; }
    /// <summary>Policy scope (global, domain, product).</summary>
    public GovernancePolicyScope Scope { get; init; } = GovernancePolicyScope.Global;
    /// <summary>Scope identifier (domain/product ID if applicable).</summary>
    public string? ScopeId { get; init; }
    /// <summary>Policy rules.</summary>
    public PolicyRule[] Rules { get; init; } = [];
    /// <summary>Whether the policy is enforced.</summary>
    public bool IsEnforced { get; init; } = true;
    /// <summary>Creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Governance policy type.
/// </summary>
public enum GovernancePolicyType
{
    /// <summary>Data quality policy.</summary>
    DataQuality,
    /// <summary>Data access policy.</summary>
    DataAccess,
    /// <summary>Data retention policy.</summary>
    DataRetention,
    /// <summary>Data classification policy.</summary>
    DataClassification,
    /// <summary>Data lineage policy.</summary>
    DataLineage,
    /// <summary>Compliance policy.</summary>
    Compliance,
    /// <summary>Security policy.</summary>
    Security
}

/// <summary>
/// Governance policy scope.
/// </summary>
public enum GovernancePolicyScope
{
    /// <summary>Global scope - applies to all domains.</summary>
    Global,
    /// <summary>Domain scope - applies to a specific domain.</summary>
    Domain,
    /// <summary>Product scope - applies to a specific product.</summary>
    Product
}

/// <summary>
/// Represents a policy rule.
/// </summary>
public sealed record PolicyRule
{
    /// <summary>Rule identifier.</summary>
    public required string RuleId { get; init; }
    /// <summary>Rule name.</summary>
    public required string Name { get; init; }
    /// <summary>Rule condition expression.</summary>
    public required string Condition { get; init; }
    /// <summary>Action on violation.</summary>
    public ViolationAction Action { get; init; } = ViolationAction.Warn;
}

/// <summary>
/// Violation action.
/// </summary>
public enum ViolationAction
{
    /// <summary>Log the violation.</summary>
    Log,
    /// <summary>Warn about the violation.</summary>
    Warn,
    /// <summary>Block the operation.</summary>
    Block,
    /// <summary>Quarantine the data.</summary>
    Quarantine
}

#endregion
