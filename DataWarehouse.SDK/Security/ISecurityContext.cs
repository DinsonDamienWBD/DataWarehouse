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
        // Cat 11 (finding 587): default is null for backward compatibility with implementations that pre-date
        // the CommandIdentity hierarchy feature. Callers MUST null-check before using for hierarchy evaluation;
        // null means "context does not carry hierarchy information — fall back to flat identity checks".
        CommandIdentity? CommandIdentity => null;
    }
}
