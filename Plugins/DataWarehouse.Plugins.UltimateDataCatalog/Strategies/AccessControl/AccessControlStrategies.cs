namespace DataWarehouse.Plugins.UltimateDataCatalog.Strategies.AccessControl;

/// <summary>
/// Role-Based Access Control Strategy - RBAC for catalog.
/// Implements T128.6: Access control integration.
/// </summary>
public sealed class RoleBasedAccessControlStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "role-based-access-control";
    public override string DisplayName => "Role-Based Access Control";
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
        "Role-based access control for catalog assets with predefined roles (Admin, Steward, Analyst, Viewer) " +
        "and customizable permissions for view, edit, approve, and delete operations.";
    public override string[] Tags => ["rbac", "roles", "permissions", "authorization", "security"];
}

/// <summary>
/// Attribute-Based Access Control Strategy - ABAC for catalog.
/// Implements T128.6: Access control integration.
/// </summary>
public sealed class AttributeBasedAccessControlStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "attribute-based-access-control";
    public override string DisplayName => "Attribute-Based Access Control";
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
        "Fine-grained attribute-based access control using user attributes (department, clearance, project) " +
        "and data attributes (classification, domain, sensitivity) for dynamic policy evaluation.";
    public override string[] Tags => ["abac", "attributes", "policies", "fine-grained", "dynamic"];
}

/// <summary>
/// Data Access Policy Sync Strategy - Syncs access policies.
/// Implements T128.6: Access control integration.
/// </summary>
public sealed class DataAccessPolicySyncStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "data-access-policy-sync";
    public override string DisplayName => "Data Access Policy Sync";
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
        "Synchronizes access control policies between the catalog and underlying data platforms including " +
        "Ranger, Sentry, Lake Formation, and Unity Catalog for consistent enforcement.";
    public override string[] Tags => ["sync", "ranger", "sentry", "lake-formation", "unity-catalog"];
}

/// <summary>
/// Access Request Workflow Strategy - Self-service access requests.
/// Implements T128.6: Access control integration.
/// </summary>
public sealed class AccessRequestWorkflowStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "access-request-workflow";
    public override string DisplayName => "Access Request Workflow";
    public override DataCatalogCategory Category => DataCatalogCategory.AccessControl;
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
        "Self-service access request workflows with multi-level approvals, time-limited access, " +
        "justification requirements, and automatic provisioning upon approval.";
    public override string[] Tags => ["access-request", "workflow", "approval", "self-service", "provisioning"];
}

/// <summary>
/// Data Masking Integration Strategy - Column-level masking.
/// Implements T128.6: Access control integration.
/// </summary>
public sealed class DataMaskingIntegrationStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "data-masking-integration";
    public override string DisplayName => "Data Masking Integration";
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
        "Integrates with data masking systems to define column-level masking policies (hash, redact, " +
        "tokenize, partial mask) based on user roles and data classification.";
    public override string[] Tags => ["masking", "pii", "redaction", "tokenization", "column-level"];
}

/// <summary>
/// Row-Level Security Strategy - Row-based filtering.
/// Implements T128.6: Access control integration.
/// </summary>
public sealed class RowLevelSecurityStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "row-level-security";
    public override string DisplayName => "Row-Level Security";
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
        "Defines row-level security policies that filter data based on user context (region, department, " +
        "customer assignment). Pushes filters to query engines.";
    public override string[] Tags => ["row-level", "filtering", "multi-tenant", "security-policies"];
}

/// <summary>
/// SSO Integration Strategy - Single sign-on integration.
/// Implements T128.6: Access control integration.
/// </summary>
public sealed class SsoIntegrationStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "sso-integration";
    public override string DisplayName => "SSO Integration";
    public override DataCatalogCategory Category => DataCatalogCategory.AccessControl;
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
        "Integrates with enterprise identity providers (Okta, Azure AD, LDAP, SAML) for single sign-on " +
        "authentication and group membership synchronization.";
    public override string[] Tags => ["sso", "okta", "azure-ad", "ldap", "saml", "oidc"];
}

/// <summary>
/// Access Audit Trail Strategy - Comprehensive access logging.
/// Implements T128.6: Access control integration.
/// </summary>
public sealed class AccessAuditTrailStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "access-audit-trail";
    public override string DisplayName => "Access Audit Trail";
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
        "Comprehensive audit trail of all catalog access including searches, views, downloads, and changes. " +
        "Supports compliance reporting and access pattern analysis.";
    public override string[] Tags => ["audit", "logging", "compliance", "trail", "forensics"];
}

/// <summary>
/// Data Privacy Compliance Strategy - GDPR/CCPA compliance.
/// Implements T128.6: Access control integration.
/// </summary>
public sealed class DataPrivacyComplianceStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "data-privacy-compliance";
    public override string DisplayName => "Data Privacy Compliance";
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
        "Enforces data privacy regulations (GDPR, CCPA, HIPAA) with consent tracking, data subject requests, " +
        "retention policies, and cross-border transfer controls.";
    public override string[] Tags => ["gdpr", "ccpa", "hipaa", "privacy", "consent", "dsr"];
}

/// <summary>
/// Temporary Access Grant Strategy - Time-limited access.
/// Implements T128.6: Access control integration.
/// </summary>
public sealed class TemporaryAccessGrantStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "temporary-access-grant";
    public override string DisplayName => "Temporary Access Grants";
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
        "Time-limited access grants with automatic expiration, renewal workflows, and break-glass emergency " +
        "access with enhanced auditing and notification.";
    public override string[] Tags => ["temporary", "time-limited", "expiration", "break-glass", "emergency"];
}
