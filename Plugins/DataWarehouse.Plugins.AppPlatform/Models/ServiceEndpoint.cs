namespace DataWarehouse.Plugins.AppPlatform.Models;

/// <summary>
/// Represents the operational status of a platform service endpoint.
/// Used by service discovery to communicate current availability.
/// </summary>
public enum ServiceStatus
{
    /// <summary>
    /// The service is fully operational and accepting requests.
    /// </summary>
    Available,

    /// <summary>
    /// The service is operational but experiencing reduced performance or partial functionality.
    /// </summary>
    Degraded,

    /// <summary>
    /// The service is not operational and cannot accept requests.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The service is undergoing scheduled maintenance and will return to operation.
    /// </summary>
    Maintenance
}

/// <summary>
/// Describes a consumable service exposed by the DataWarehouse platform. Each endpoint
/// defines the message bus topic for communication, the scopes required for access,
/// and the operations the service supports.
/// </summary>
/// <remarks>
/// <para>
/// Service endpoints are returned by <see cref="PlatformServiceCatalog"/> via the
/// <c>platform.services.list</c> topic to allow registered applications to discover
/// all available services and their access requirements.
/// </para>
/// <para>
/// Applications can filter the catalog by comparing their token scopes against
/// <see cref="RequiredScopes"/> to determine which services they can access.
/// </para>
/// </remarks>
public sealed record ServiceEndpoint
{
    /// <summary>
    /// Unique machine-friendly name of the service (e.g., "storage", "intelligence", "accesscontrol").
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Human-readable display name for the service (e.g., "Storage Service").
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Description of what the service provides and its primary capabilities.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Message bus topic used to send requests to this service
    /// (e.g., "platform.service.storage").
    /// </summary>
    public required string RequestTopic { get; init; }

    /// <summary>
    /// Scopes that a service token must have to access this service.
    /// Token validation checks that at least the required scopes are present.
    /// </summary>
    public required string[] RequiredScopes { get; init; }

    /// <summary>
    /// Operations this service supports (e.g., "read", "write", "delete", "list").
    /// Applications can use this to discover available functionality.
    /// </summary>
    public required string[] SupportedOperations { get; init; }

    /// <summary>
    /// Whether this service requires an AI workflow configuration to be set up
    /// before it can be used. Defaults to <c>false</c>.
    /// </summary>
    public bool RequiresAiWorkflow { get; init; }

    /// <summary>
    /// Current operational status of this service. Defaults to <see cref="ServiceStatus.Available"/>.
    /// </summary>
    public ServiceStatus Status { get; init; } = ServiceStatus.Available;
}

/// <summary>
/// Represents the complete catalog of platform services available for consumption
/// by registered applications. Returned by the <c>platform.services.list</c> topic.
/// </summary>
/// <remarks>
/// The catalog includes the platform version and generation timestamp, allowing
/// clients to cache the catalog and detect changes via <see cref="GeneratedAt"/>.
/// </remarks>
public sealed record PlatformServiceCatalog
{
    /// <summary>
    /// Array of all service endpoints available on the platform.
    /// </summary>
    public required ServiceEndpoint[] Services { get; init; }

    /// <summary>
    /// Platform version string (e.g., "1.0.0") for compatibility checking.
    /// </summary>
    public required string PlatformVersion { get; init; }

    /// <summary>
    /// UTC timestamp when this catalog was generated.
    /// </summary>
    public required DateTime GeneratedAt { get; init; }
}
