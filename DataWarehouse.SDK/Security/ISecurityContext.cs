namespace DataWarehouse.SDK.Security
{
    /// <summary>
    /// Represents the caller (User, Module, or System Service).
    /// </summary>
    public interface ISecurityContext
    {
        /// <summary>
        /// Name
        /// </summary>
        string UserId { get; }

        /// <summary>
        /// Tenant ID
        /// </summary>
        string? TenantId { get; }

        /// <summary>
        /// Roles
        /// </summary>
        IEnumerable<string> Roles { get; }

        /// <summary>
        /// Is system admin
        /// </summary>
        bool IsSystemAdmin { get; }

        /// <summary>
        /// The full command identity for multi-level access verification.
        /// When available, this provides the complete hierarchy context
        /// (System → Tenant → Instance → UserGroup → User) for access decisions.
        /// </summary>
        CommandIdentity? CommandIdentity => null; // Default interface method for backward compatibility
    }
}
