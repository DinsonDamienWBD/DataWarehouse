using DataWarehouse.SDK.Security;

namespace DataWarehouse.Kernel.Security
{
    /// <summary>
    /// Default security context for single-user/laptop mode.
    /// Represents the local user with admin privileges.
    /// </summary>
    public sealed class LocalSecurityContext : ISecurityContext
    {
        public string UserId { get; }
        public string? TenantId { get; }
        public IEnumerable<string> Roles { get; }
        public bool IsSystemAdmin { get; }

        public LocalSecurityContext(
            string? userId = null,
            string? tenantId = null,
            IEnumerable<string>? roles = null,
            bool isSystemAdmin = true)
        {
            UserId = userId ?? Environment.UserName;
            TenantId = tenantId ?? "local";
            Roles = roles ?? ["admin", "user"];
            IsSystemAdmin = isSystemAdmin;
        }

        /// <summary>
        /// Default local admin context.
        /// </summary>
        public static LocalSecurityContext Default { get; } = new();

        /// <summary>
        /// System context for internal kernel operations.
        /// </summary>
        public static LocalSecurityContext System { get; } = new(
            userId: "system",
            tenantId: "system",
            roles: ["system"],
            isSystemAdmin: true);
    }

    /// <summary>
    /// Provides security context for the current operation.
    /// Supports ambient context pattern for propagation.
    /// </summary>
    public sealed class SecurityContextProvider
    {
        private static readonly AsyncLocal<ISecurityContext?> _ambientContext = new();

        /// <summary>
        /// Gets the current security context.
        /// Returns the ambient context if set, otherwise the default local context.
        /// </summary>
        public static ISecurityContext Current =>
            _ambientContext.Value ?? LocalSecurityContext.Default;

        /// <summary>
        /// Sets the ambient security context for the current async flow.
        /// </summary>
        public static IDisposable SetContext(ISecurityContext context)
        {
            var previous = _ambientContext.Value;
            _ambientContext.Value = context;
            return new ContextScope(previous);
        }

        /// <summary>
        /// Runs an action with a specific security context.
        /// </summary>
        public static async Task<T> RunWithContextAsync<T>(
            ISecurityContext context,
            Func<Task<T>> action)
        {
            using (SetContext(context))
            {
                return await action();
            }
        }

        /// <summary>
        /// Runs an action with a specific security context.
        /// </summary>
        public static async Task RunWithContextAsync(
            ISecurityContext context,
            Func<Task> action)
        {
            using (SetContext(context))
            {
                await action();
            }
        }

        /// <summary>
        /// Runs an action with system context.
        /// </summary>
        public static async Task<T> RunAsSystemAsync<T>(Func<Task<T>> action)
        {
            return await RunWithContextAsync(LocalSecurityContext.System, action);
        }

        /// <summary>
        /// Runs an action with system context.
        /// </summary>
        public static async Task RunAsSystemAsync(Func<Task> action)
        {
            await RunWithContextAsync(LocalSecurityContext.System, action);
        }

        private sealed class ContextScope : IDisposable
        {
            private readonly ISecurityContext? _previous;
            private bool _disposed;

            public ContextScope(ISecurityContext? previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _ambientContext.Value = _previous;
            }
        }
    }

    /// <summary>
    /// Extensions for security context operations.
    /// </summary>
    public static class SecurityContextExtensions
    {
        /// <summary>
        /// Checks if the context has a specific role.
        /// </summary>
        public static bool HasRole(this ISecurityContext context, string role)
        {
            return context.Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if the context has any of the specified roles.
        /// </summary>
        public static bool HasAnyRole(this ISecurityContext context, params string[] roles)
        {
            return roles.Any(r => context.HasRole(r));
        }

        /// <summary>
        /// Checks if the context has all of the specified roles.
        /// </summary>
        public static bool HasAllRoles(this ISecurityContext context, params string[] roles)
        {
            return roles.All(r => context.HasRole(r));
        }

        /// <summary>
        /// Checks if the context can perform admin operations.
        /// </summary>
        public static bool CanAdmin(this ISecurityContext context)
        {
            return context.IsSystemAdmin || context.HasRole("admin");
        }

        /// <summary>
        /// Creates an audit record for this context.
        /// </summary>
        public static Dictionary<string, object> ToAuditRecord(this ISecurityContext context)
        {
            return new Dictionary<string, object>
            {
                ["UserId"] = context.UserId,
                ["TenantId"] = context.TenantId ?? "none",
                ["Roles"] = string.Join(",", context.Roles),
                ["IsSystemAdmin"] = context.IsSystemAdmin,
                ["Timestamp"] = DateTime.UtcNow
            };
        }
    }
}
