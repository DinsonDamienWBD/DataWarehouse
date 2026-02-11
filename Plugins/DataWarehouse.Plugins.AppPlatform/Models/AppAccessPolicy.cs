namespace DataWarehouse.Plugins.AppPlatform.Models;

/// <summary>
/// Access control model types supported for per-app policy configuration.
/// Each model defines a different approach to authorization decision-making.
/// </summary>
public enum AccessControlModel
{
    /// <summary>
    /// Role-Based Access Control: permissions assigned to roles, users assigned to roles.
    /// </summary>
    RBAC,

    /// <summary>
    /// Attribute-Based Access Control: permissions evaluated based on attribute conditions.
    /// </summary>
    ABAC,

    /// <summary>
    /// Mandatory Access Control: system-enforced security labels and clearance levels.
    /// </summary>
    MAC,

    /// <summary>
    /// Discretionary Access Control: resource owners control access permissions.
    /// </summary>
    DAC,

    /// <summary>
    /// Policy-Based Access Control: centralized policy rules govern access decisions.
    /// </summary>
    PBAC
}

/// <summary>
/// Comparison operators used in attribute-based access control evaluations.
/// </summary>
public enum AttributeOperator
{
    /// <summary>
    /// Attribute value must equal the expected value exactly.
    /// </summary>
    Equals,

    /// <summary>
    /// Attribute value must not equal the expected value.
    /// </summary>
    NotEquals,

    /// <summary>
    /// Attribute value must contain the expected value as a substring.
    /// </summary>
    Contains,

    /// <summary>
    /// Attribute value must be numerically greater than the expected value.
    /// </summary>
    GreaterThan,

    /// <summary>
    /// Attribute value must be numerically less than the expected value.
    /// </summary>
    LessThan,

    /// <summary>
    /// Attribute value must be a member of the expected set of values.
    /// </summary>
    In,

    /// <summary>
    /// Attribute value must not be a member of the expected set of values.
    /// </summary>
    NotIn
}

/// <summary>
/// Defines a role within an application's RBAC policy, including the permissions granted to that role.
/// </summary>
public sealed record AppRole
{
    /// <summary>
    /// Name of the role (e.g., "admin", "reader", "contributor").
    /// </summary>
    public required string RoleName { get; init; }

    /// <summary>
    /// Permissions granted to this role (e.g., "read", "write", "delete", "manage").
    /// </summary>
    public required string[] Permissions { get; init; }

    /// <summary>
    /// Optional human-readable description of the role's purpose.
    /// </summary>
    public string? Description { get; init; }
}

/// <summary>
/// Defines an attribute condition for ABAC policy evaluation.
/// Attributes are evaluated using the specified operator against the expected value.
/// </summary>
public sealed record AppAttribute
{
    /// <summary>
    /// Name of the attribute to evaluate (e.g., "department", "clearance_level", "location").
    /// </summary>
    public required string AttributeName { get; init; }

    /// <summary>
    /// Expected value to compare the attribute against.
    /// </summary>
    public required string AttributeValue { get; init; }

    /// <summary>
    /// Comparison operator used to evaluate the attribute. Defaults to <see cref="AttributeOperator.Equals"/>.
    /// </summary>
    public AttributeOperator Operator { get; init; } = AttributeOperator.Equals;
}

/// <summary>
/// Represents a per-application access control policy configuration.
/// Each registered application can have its own RBAC/ABAC/MAC/DAC/PBAC policy rules
/// that are bound into UltimateAccessControl via the message bus.
/// </summary>
/// <remarks>
/// Policies support five access control models. RBAC policies use <see cref="Roles"/>
/// to define role-permission mappings. ABAC policies use <see cref="Attributes"/> for
/// condition-based evaluation. MAC, DAC, and PBAC policies use the model-specific
/// evaluation logic within UltimateAccessControl.
/// Tenant isolation is enforced by default and can be relaxed per-app for cross-tenant scenarios.
/// </remarks>
public sealed record AppAccessPolicy
{
    /// <summary>
    /// Identifier of the application this policy belongs to.
    /// </summary>
    public required string AppId { get; init; }

    /// <summary>
    /// Unique identifier for this policy, generated on creation.
    /// </summary>
    public required string PolicyId { get; init; }

    /// <summary>
    /// The access control model used by this policy (RBAC, ABAC, MAC, DAC, or PBAC).
    /// </summary>
    public AccessControlModel Model { get; init; } = AccessControlModel.RBAC;

    /// <summary>
    /// Role definitions for RBAC-based policies. Each role maps a name to a set of permissions.
    /// </summary>
    public required AppRole[] Roles { get; init; }

    /// <summary>
    /// Attribute conditions for ABAC-based policies. Each attribute defines a condition to evaluate.
    /// </summary>
    public required AppAttribute[] Attributes { get; init; }

    /// <summary>
    /// Whether to enforce strict tenant isolation for this application. Defaults to <c>true</c>.
    /// </summary>
    public bool EnforceTenantIsolation { get; init; } = true;

    /// <summary>
    /// Whether this application is allowed to access resources in other tenants. Defaults to <c>false</c>.
    /// </summary>
    public bool AllowCrossTenantAccess { get; init; }

    /// <summary>
    /// List of application identifiers that this application is allowed to access cross-tenant.
    /// Only applicable when <see cref="AllowCrossTenantAccess"/> is <c>true</c>.
    /// </summary>
    public string[] AllowedCrossTenantApps { get; init; } = [];

    /// <summary>
    /// UTC timestamp when this policy was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// UTC timestamp of the most recent update, or <c>null</c> if never updated.
    /// </summary>
    public DateTime? UpdatedAt { get; init; }
}
