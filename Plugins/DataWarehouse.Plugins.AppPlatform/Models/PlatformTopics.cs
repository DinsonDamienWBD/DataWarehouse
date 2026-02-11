namespace DataWarehouse.Plugins.AppPlatform.Models;

/// <summary>
/// Message bus topic constants for all Application Platform operations.
/// All topics use the "platform" prefix to namespace platform messages
/// and avoid collisions with other plugin topics.
/// </summary>
internal static class PlatformTopics
{
    /// <summary>
    /// Common prefix for all platform message bus topics.
    /// </summary>
    public const string Prefix = "platform";

    // ========================================
    // App Lifecycle Topics
    // ========================================

    /// <summary>
    /// Topic for registering a new application on the platform.
    /// </summary>
    public const string AppRegister = $"{Prefix}.register";

    /// <summary>
    /// Topic for deregistering (deleting) an application from the platform.
    /// </summary>
    public const string AppDeregister = $"{Prefix}.deregister";

    /// <summary>
    /// Topic for updating an existing application registration.
    /// </summary>
    public const string AppUpdate = $"{Prefix}.update";

    /// <summary>
    /// Topic for retrieving a single application registration by ID.
    /// </summary>
    public const string AppGet = $"{Prefix}.get";

    /// <summary>
    /// Topic for listing all active application registrations.
    /// </summary>
    public const string AppList = $"{Prefix}.list";

    // ========================================
    // Token Management Topics
    // ========================================

    /// <summary>
    /// Topic for creating a new service token for an application.
    /// </summary>
    public const string TokenCreate = $"{Prefix}.token.create";

    /// <summary>
    /// Topic for rotating an existing service token (revoke old, create new).
    /// </summary>
    public const string TokenRotate = $"{Prefix}.token.rotate";

    /// <summary>
    /// Topic for revoking a service token.
    /// </summary>
    public const string TokenRevoke = $"{Prefix}.token.revoke";

    /// <summary>
    /// Topic for validating a raw service token key.
    /// </summary>
    public const string TokenValidate = $"{Prefix}.token.validate";

    // ========================================
    // Service Routing Topics
    // ========================================

    /// <summary>
    /// Topic for routing requests to the storage service.
    /// </summary>
    public const string ServiceStorage = $"{Prefix}.service.storage";

    /// <summary>
    /// Topic for routing requests to the access control service.
    /// </summary>
    public const string ServiceAccessControl = $"{Prefix}.service.accesscontrol";

    /// <summary>
    /// Topic for routing requests to the intelligence (AI) service.
    /// </summary>
    public const string ServiceIntelligence = $"{Prefix}.service.intelligence";

    /// <summary>
    /// Topic for routing requests to the observability service.
    /// </summary>
    public const string ServiceObservability = $"{Prefix}.service.observability";

    /// <summary>
    /// Topic for routing requests to the replication service.
    /// </summary>
    public const string ServiceReplication = $"{Prefix}.service.replication";

    /// <summary>
    /// Topic for routing requests to the compliance service.
    /// </summary>
    public const string ServiceCompliance = $"{Prefix}.service.compliance";
}
