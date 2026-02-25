using DataWarehouse.SDK.Contracts.DataMesh;

namespace DataWarehouse.Plugins.UltimateDataMesh.Strategies.CrossDomainSharing;

/// <summary>
/// Data Sharing Agreement Strategy - Formal sharing agreements.
/// Implements T113.6: Cross-domain data sharing.
/// </summary>
public sealed class DataSharingAgreementStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "data-sharing-agreement";
    public override string DisplayName => "Data Sharing Agreements";
    public override DataMeshCategory Category => DataMeshCategory.CrossDomainSharing;
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
        "Formal data sharing agreements between domains with terms, conditions, expiration, " +
        "and audit trails. Supports approval workflows and automated agreement enforcement.";
    public override string[] Tags => ["agreement", "sharing", "terms", "approval", "audit"];
}

/// <summary>
/// Data Federation Strategy - Federated data access across domains.
/// Implements T113.6: Cross-domain data sharing.
/// </summary>
public sealed class DataFederationStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "data-federation";
    public override string DisplayName => "Data Federation";
    public override DataMeshCategory Category => DataMeshCategory.CrossDomainSharing;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = true,
        SupportsEventDriven = false,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Federated data access enabling queries across domain boundaries without data movement. " +
        "Supports query federation, virtual views, and distributed query optimization.";
    public override string[] Tags => ["federation", "virtual", "distributed", "query", "cross-domain"];
}

/// <summary>
/// Data Contract Sharing Strategy - Contract-based sharing.
/// Implements T113.6: Cross-domain data sharing.
/// </summary>
public sealed class DataContractSharingStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "data-contract-sharing";
    public override string DisplayName => "Contract-Based Sharing";
    public override DataMeshCategory Category => DataMeshCategory.CrossDomainSharing;
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
        "Contract-based data sharing with schema contracts, SLA agreements, and change notification. " +
        "Ensures consumer-producer alignment and backward compatibility guarantees.";
    public override string[] Tags => ["contract", "schema", "sla", "backward-compatibility", "producer-consumer"];
}

/// <summary>
/// Event-Based Sharing Strategy - Real-time event sharing.
/// Implements T113.6: Cross-domain data sharing.
/// </summary>
public sealed class EventBasedSharingStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "event-based-sharing";
    public override string DisplayName => "Event-Based Sharing";
    public override DataMeshCategory Category => DataMeshCategory.CrossDomainSharing;
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
        "Real-time event-based data sharing using event streams (Kafka, Pulsar). " +
        "Supports pub/sub, event sourcing, and Change Data Capture for cross-domain sync.";
    public override string[] Tags => ["events", "streaming", "kafka", "pub-sub", "cdc"];
}

/// <summary>
/// API Gateway Sharing Strategy - API-based cross-domain sharing.
/// Implements T113.6: Cross-domain data sharing.
/// </summary>
public sealed class ApiGatewaySharingStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "api-gateway-sharing";
    public override string DisplayName => "API Gateway Sharing";
    public override DataMeshCategory Category => DataMeshCategory.CrossDomainSharing;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = true,
        SupportsEventDriven = false,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Centralized API gateway for cross-domain data access with rate limiting, authentication, " +
        "request transformation, and unified access control across all domains.";
    public override string[] Tags => ["api", "gateway", "rate-limiting", "authentication", "transformation"];
}

/// <summary>
/// Data Virtualization Strategy - Virtual data layer for sharing.
/// Implements T113.6: Cross-domain data sharing.
/// </summary>
public sealed class DataVirtualizationStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "data-virtualization";
    public override string DisplayName => "Data Virtualization";
    public override DataMeshCategory Category => DataMeshCategory.CrossDomainSharing;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = true,
        SupportsEventDriven = false,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Data virtualization layer providing unified access to cross-domain data without replication. " +
        "Supports virtual tables, materialized views, and intelligent caching.";
    public override string[] Tags => ["virtualization", "virtual-tables", "caching", "unified-access", "no-replication"];
}

/// <summary>
/// Data Mesh Bridge Strategy - Bridge between mesh domains.
/// Implements T113.6: Cross-domain data sharing.
/// </summary>
public sealed class DataMeshBridgeStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "data-mesh-bridge";
    public override string DisplayName => "Data Mesh Bridge";
    public override DataMeshCategory Category => DataMeshCategory.CrossDomainSharing;
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
        "Dedicated bridge infrastructure connecting mesh domains with protocol translation, " +
        "data transformation, and secure tunneling for cross-mesh data sharing.";
    public override string[] Tags => ["bridge", "tunneling", "protocol-translation", "cross-mesh", "secure"];
}
