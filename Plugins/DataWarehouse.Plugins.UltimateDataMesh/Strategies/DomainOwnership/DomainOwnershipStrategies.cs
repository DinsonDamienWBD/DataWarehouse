using DataWarehouse.SDK.Contracts.DataMesh;

namespace DataWarehouse.Plugins.UltimateDataMesh.Strategies.DomainOwnership;

/// <summary>
/// Domain Team Autonomy Strategy - Enables domain teams to fully own their data.
/// Implements T113.1: Domain-oriented data ownership.
/// </summary>
public sealed class DomainTeamAutonomyStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "domain-team-autonomy";
    public override string DisplayName => "Domain Team Autonomy";
    public override DataMeshCategory Category => DataMeshCategory.DomainOwnership;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = true,
        SupportsEventDriven = true,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0, // Unlimited
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Enables domain teams to have full autonomy over their data, including schema design, access control, " +
        "versioning, and lifecycle management. Teams own data from creation to retirement.";
    public override string[] Tags => ["autonomy", "ownership", "team", "decentralized", "self-service"];
}

/// <summary>
/// Domain Bounded Context Strategy - Aligns data ownership with DDD bounded contexts.
/// Implements T113.1: Domain-oriented data ownership.
/// </summary>
public sealed class DomainBoundedContextStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "domain-bounded-context";
    public override string DisplayName => "Bounded Context Alignment";
    public override DataMeshCategory Category => DataMeshCategory.DomainOwnership;
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
        "Aligns data domain boundaries with Domain-Driven Design bounded contexts. Ensures data ownership " +
        "reflects business capabilities and maintains clear context mapping between domains.";
    public override string[] Tags => ["ddd", "bounded-context", "context-mapping", "ubiquitous-language", "aggregates"];
}

/// <summary>
/// Domain Lifecycle Management Strategy - Manages domain lifecycle from creation to retirement.
/// Implements T113.1: Domain-oriented data ownership.
/// </summary>
public sealed class DomainLifecycleStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "domain-lifecycle";
    public override string DisplayName => "Domain Lifecycle Management";
    public override DataMeshCategory Category => DataMeshCategory.DomainOwnership;
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
        "Comprehensive domain lifecycle management including provisioning, activation, suspension, " +
        "decommissioning, and archival. Supports domain transitions with data migration and consumer notification.";
    public override string[] Tags => ["lifecycle", "provisioning", "decommissioning", "migration", "archival"];
}

/// <summary>
/// Domain Ownership Transfer Strategy - Enables secure transfer of domain ownership.
/// Implements T113.1: Domain-oriented data ownership.
/// </summary>
public sealed class DomainOwnershipTransferStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "domain-ownership-transfer";
    public override string DisplayName => "Ownership Transfer";
    public override DataMeshCategory Category => DataMeshCategory.DomainOwnership;
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
        "Secure and audited transfer of domain ownership between teams with approval workflows, " +
        "knowledge transfer documentation, and seamless handover of responsibilities and access rights.";
    public override string[] Tags => ["transfer", "handover", "workflow", "approval", "audit"];
}

/// <summary>
/// Domain Team Structure Strategy - Defines team roles and responsibilities.
/// Implements T113.1: Domain-oriented data ownership.
/// </summary>
public sealed class DomainTeamStructureStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "domain-team-structure";
    public override string DisplayName => "Team Structure";
    public override DataMeshCategory Category => DataMeshCategory.DomainOwnership;
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
        "Defines domain team structure with clear roles: data owner, data steward, data engineer, " +
        "and data consumer representative. Supports RACI matrices and responsibility escalation paths.";
    public override string[] Tags => ["team", "roles", "raci", "steward", "responsibility"];
}

/// <summary>
/// Domain Data Sovereignty Strategy - Ensures data sovereignty within domain boundaries.
/// Implements T113.1: Domain-oriented data ownership.
/// </summary>
public sealed class DomainDataSovereigntyStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "domain-data-sovereignty";
    public override string DisplayName => "Data Sovereignty";
    public override DataMeshCategory Category => DataMeshCategory.DomainOwnership;
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
        "Enforces data sovereignty within domain boundaries, ensuring data remains under domain control " +
        "with geo-fencing, regulatory compliance, and cross-border data transfer restrictions.";
    public override string[] Tags => ["sovereignty", "compliance", "geo-fencing", "regulatory", "control"];
}
