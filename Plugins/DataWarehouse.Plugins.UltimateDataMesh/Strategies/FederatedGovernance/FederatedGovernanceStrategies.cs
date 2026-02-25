using DataWarehouse.SDK.Contracts.DataMesh;

namespace DataWarehouse.Plugins.UltimateDataMesh.Strategies.FederatedGovernance;

/// <summary>
/// Federated Policy Management Strategy - Distributed policy governance.
/// Implements T113.4: Federated computational governance.
/// </summary>
public sealed class FederatedPolicyManagementStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "federated-policy-management";
    public override string DisplayName => "Federated Policy Management";
    public override DataMeshCategory Category => DataMeshCategory.FederatedGovernance;
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
        "Federated policy management allowing global policies with domain-specific overrides. " +
        "Supports policy inheritance, conflict resolution, and automated compliance checking.";
    public override string[] Tags => ["federated", "policy", "governance", "inheritance", "compliance"];
}

/// <summary>
/// Computational Governance Strategy - Code-based governance enforcement.
/// Implements T113.4: Federated computational governance.
/// </summary>
public sealed class ComputationalGovernanceStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "computational-governance";
    public override string DisplayName => "Computational Governance";
    public override DataMeshCategory Category => DataMeshCategory.FederatedGovernance;
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
        "Computational governance using policy-as-code with OPA/Rego, automated enforcement, " +
        "real-time policy evaluation, and automated remediation for violations.";
    public override string[] Tags => ["computational", "policy-as-code", "opa", "rego", "automation"];
}

/// <summary>
/// Global Standards Strategy - Organization-wide data standards.
/// Implements T113.4: Federated computational governance.
/// </summary>
public sealed class GlobalStandardsStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "global-standards";
    public override string DisplayName => "Global Standards";
    public override DataMeshCategory Category => DataMeshCategory.FederatedGovernance;
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
        "Global data standards including naming conventions, data types, classification taxonomies, " +
        "and interoperability requirements that all domains must follow.";
    public override string[] Tags => ["standards", "naming", "taxonomy", "interoperability", "conventions"];
}

/// <summary>
/// Interoperability Standards Strategy - Cross-domain interoperability rules.
/// Implements T113.4: Federated computational governance.
/// </summary>
public sealed class InteroperabilityStandardsStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "interoperability-standards";
    public override string DisplayName => "Interoperability Standards";
    public override DataMeshCategory Category => DataMeshCategory.FederatedGovernance;
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
        "Interoperability standards ensuring seamless data exchange between domains with " +
        "standard protocols, data formats, and API contracts for cross-domain integration.";
    public override string[] Tags => ["interoperability", "protocols", "formats", "api-contracts", "integration"];
}

/// <summary>
/// Compliance Framework Strategy - Regulatory compliance framework.
/// Implements T113.4: Federated computational governance.
/// </summary>
public sealed class ComplianceFrameworkStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "compliance-framework";
    public override string DisplayName => "Compliance Framework";
    public override DataMeshCategory Category => DataMeshCategory.FederatedGovernance;
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
        "Regulatory compliance framework supporting GDPR, CCPA, HIPAA, SOX, and other regulations " +
        "with automated compliance checking, reporting, and remediation workflows.";
    public override string[] Tags => ["compliance", "gdpr", "ccpa", "hipaa", "regulatory"];
}

/// <summary>
/// Data Classification Strategy - Automated data classification.
/// Implements T113.4: Federated computational governance.
/// </summary>
public sealed class DataClassificationGovernanceStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "data-classification-governance";
    public override string DisplayName => "Data Classification";
    public override DataMeshCategory Category => DataMeshCategory.FederatedGovernance;
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
        "Federated data classification with ML-powered auto-classification, PII detection, " +
        "sensitivity labeling, and classification propagation across data transformations.";
    public override string[] Tags => ["classification", "pii", "sensitivity", "ml", "labeling"];
}

/// <summary>
/// Governance Council Strategy - Virtual governance council management.
/// Implements T113.4: Federated computational governance.
/// </summary>
public sealed class GovernanceCouncilStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "governance-council";
    public override string DisplayName => "Governance Council";
    public override DataMeshCategory Category => DataMeshCategory.FederatedGovernance;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = false,
        SupportsBatch = false,
        SupportsEventDriven = true,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Virtual governance council with domain representatives for policy decisions, " +
        "exception approvals, and cross-domain conflict resolution with voting mechanisms.";
    public override string[] Tags => ["council", "voting", "approval", "exception", "conflict-resolution"];
}
