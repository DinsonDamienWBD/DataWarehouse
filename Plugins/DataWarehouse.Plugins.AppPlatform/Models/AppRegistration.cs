namespace DataWarehouse.Plugins.AppPlatform.Models;

/// <summary>
/// Represents a registered application in the DataWarehouse platform.
/// Each registration maps to a unique tenant in the access control system
/// and provides per-app isolation for all DW services.
/// </summary>
public sealed record AppRegistration
{
    /// <summary>
    /// Unique identifier for the registered application, generated as a GUID without hyphens.
    /// </summary>
    public required string AppId { get; init; }

    /// <summary>
    /// Short name of the application used for identification and display.
    /// </summary>
    public required string AppName { get; init; }

    /// <summary>
    /// Optional human-readable display name for the application.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Optional description of the application's purpose and functionality.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Identifier of the user who owns and registered this application.
    /// </summary>
    public required string OwnerUserId { get; init; }

    /// <summary>
    /// URLs that the platform may call back to for notifications and webhooks.
    /// </summary>
    public required string[] CallbackUrls { get; init; }

    /// <summary>
    /// Current lifecycle status of the application registration.
    /// </summary>
    public AppStatus Status { get; init; } = AppStatus.Active;

    /// <summary>
    /// UTC timestamp when the application was first registered.
    /// </summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>
    /// UTC timestamp of the most recent update to the registration, or null if never updated.
    /// </summary>
    public DateTime? UpdatedAt { get; init; }

    /// <summary>
    /// UTC timestamp when the application was suspended, or null if not suspended.
    /// </summary>
    public DateTime? SuspendedAt { get; init; }

    /// <summary>
    /// Reason for suspension, or null if the application is not suspended.
    /// </summary>
    public string? SuspensionReason { get; init; }

    /// <summary>
    /// Service configuration defining resource limits and enabled services for this application.
    /// </summary>
    public AppServiceConfig ServiceConfig { get; init; } = new();

    /// <summary>
    /// Arbitrary metadata key-value pairs associated with the registration.
    /// </summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Lifecycle status of an application registration in the platform.
/// </summary>
public enum AppStatus
{
    /// <summary>
    /// Application is pending approval or initial setup.
    /// </summary>
    Pending,

    /// <summary>
    /// Application is active and can consume platform services.
    /// </summary>
    Active,

    /// <summary>
    /// Application is temporarily suspended and cannot consume services.
    /// </summary>
    Suspended,

    /// <summary>
    /// Application has been deactivated by the owner and is no longer operational.
    /// </summary>
    Deactivated,

    /// <summary>
    /// Application has been permanently deleted.
    /// </summary>
    Deleted
}

/// <summary>
/// Configuration for platform services available to a registered application,
/// including resource limits and enabled service scopes.
/// </summary>
public sealed record AppServiceConfig
{
    /// <summary>
    /// Maximum number of users allowed for this application.
    /// </summary>
    public int MaxUsers { get; init; } = 100;

    /// <summary>
    /// Maximum number of resources (objects, records) the application can manage.
    /// </summary>
    public int MaxResources { get; init; } = 10_000;

    /// <summary>
    /// Maximum storage in bytes allocated to this application (default: 1 GiB).
    /// </summary>
    public long MaxStorageBytes { get; init; } = 1_073_741_824;

    /// <summary>
    /// Array of service names enabled for this application.
    /// Default services include storage, access control, intelligence, and observability.
    /// </summary>
    public string[] EnabledServices { get; init; } = ["storage", "accesscontrol", "intelligence", "observability"];
}
