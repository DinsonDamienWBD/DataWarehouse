using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Contracts.Composition;

/// <summary>
/// Represents the current lifecycle state of a Data Room.
/// </summary>
[SdkCompatibility("3.0.0")]
public enum DataRoomState
{
    /// <summary>Room is being created and configured.</summary>
    Creating,

    /// <summary>Room is active and participants can access data.</summary>
    Active,

    /// <summary>Room is in the process of expiring and revoking access.</summary>
    Expiring,

    /// <summary>Room has been destroyed and all access revoked.</summary>
    Destroyed,

    /// <summary>Room creation or operation failed.</summary>
    Failed
}

/// <summary>
/// Defines the access role for a Data Room participant.
/// </summary>
[SdkCompatibility("3.0.0")]
public enum ParticipantRole
{
    /// <summary>Room creator with full control.</summary>
    Owner,

    /// <summary>Read-only access to shared datasets.</summary>
    ReadOnly,

    /// <summary>Read and write access to shared datasets.</summary>
    ReadWrite,

    /// <summary>Administrative access (can invite, remove participants).</summary>
    Admin
}

/// <summary>
/// Represents an organization participating in a Data Room.
/// </summary>
[SdkCompatibility("3.0.0")]
public sealed record DataRoomParticipant
{
    /// <summary>
    /// Unique identifier for the participating organization.
    /// </summary>
    public required string OrganizationId { get; init; }

    /// <summary>
    /// Human-readable name of the organization.
    /// </summary>
    public required string OrganizationName { get; init; }

    /// <summary>
    /// Access role assigned to this participant.
    /// </summary>
    public required ParticipantRole Role { get; init; }

    /// <summary>
    /// Timestamp when the participant was invited.
    /// </summary>
    public required DateTimeOffset InvitedAt { get; init; }

    /// <summary>
    /// Timestamp when the participant joined the room (null if invitation pending).
    /// </summary>
    public DateTimeOffset? JoinedAt { get; init; }

    /// <summary>
    /// Ephemeral access token for this participant (time-limited).
    /// </summary>
    public string? AccessToken { get; init; }

    /// <summary>
    /// Expiration time for the access token.
    /// </summary>
    public DateTimeOffset? TokenExpiresAt { get; init; }
}

/// <summary>
/// Represents a dataset shared within a Data Room.
/// </summary>
[SdkCompatibility("3.0.0")]
public sealed record DataRoomDataset
{
    /// <summary>
    /// Unique identifier for the dataset.
    /// </summary>
    public required string DatasetId { get; init; }

    /// <summary>
    /// Human-readable name of the dataset.
    /// </summary>
    public required string DatasetName { get; init; }

    /// <summary>
    /// Organization ID that owns/contributed this dataset.
    /// </summary>
    public required string OwnerId { get; init; }

    /// <summary>
    /// Geographic regions where this dataset is allowed (null = no restrictions).
    /// </summary>
    public IReadOnlyList<string>? GeofenceRegions { get; init; }

    /// <summary>
    /// Whether zero-trust access control is required for this dataset.
    /// </summary>
    public bool RequiresZeroTrust { get; init; } = true;
}

/// <summary>
/// Represents a single audit trail entry for Data Room operations.
/// </summary>
[SdkCompatibility("3.0.0")]
public sealed record DataRoomAuditEntry
{
    /// <summary>
    /// Unique identifier for this audit entry.
    /// </summary>
    public required string EntryId { get; init; }

    /// <summary>
    /// When the audited action occurred.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Identifier of the actor who performed the action.
    /// </summary>
    public required string ActorId { get; init; }

    /// <summary>
    /// Action that was performed (e.g., "created", "invited", "accessed", "expired", "destroyed").
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Identifier of the target affected by the action (dataset ID or participant ID).
    /// </summary>
    public string? TargetId { get; init; }

    /// <summary>
    /// Human-readable description of the action.
    /// </summary>
    public string? Details { get; init; }
}

/// <summary>
/// Represents a cross-organization Data Room for secure time-limited data sharing.
/// </summary>
[SdkCompatibility("3.0.0")]
public sealed record DataRoom
{
    /// <summary>
    /// Unique identifier for this Data Room.
    /// </summary>
    public required string RoomId { get; init; }

    /// <summary>
    /// Human-readable name for the room.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Current lifecycle state of the room.
    /// </summary>
    public required DataRoomState State { get; init; }

    /// <summary>
    /// When the room was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When the room expires and all access will be revoked.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Organization ID of the room creator.
    /// </summary>
    public required string CreatedBy { get; init; }

    /// <summary>
    /// Organizations participating in this room.
    /// </summary>
    public required IReadOnlyList<DataRoomParticipant> Participants { get; init; }

    /// <summary>
    /// Datasets shared within this room.
    /// </summary>
    public required IReadOnlyList<DataRoomDataset> Datasets { get; init; }

    /// <summary>
    /// Complete audit trail of all room operations.
    /// </summary>
    public required IReadOnlyList<DataRoomAuditEntry> AuditTrail { get; init; }

    /// <summary>
    /// Whether the room has expired (current time >= ExpiresAt).
    /// </summary>
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;

    /// <summary>
    /// Whether the room is currently active and not expired.
    /// </summary>
    public bool IsActive => State == DataRoomState.Active && !IsExpired;
}

/// <summary>
/// Configuration options for Data Room orchestration.
/// </summary>
[SdkCompatibility("3.0.0")]
public sealed record DataRoomConfig
{
    /// <summary>
    /// Default expiration time for new rooms.
    /// </summary>
    public TimeSpan DefaultExpiry { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How often to refresh ephemeral access tokens.
    /// </summary>
    public TimeSpan TokenRefreshInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether to enforce geofencing constraints.
    /// </summary>
    public bool EnforceGeofencing { get; init; } = true;

    /// <summary>
    /// Whether to require zero-trust access control for all datasets.
    /// </summary>
    public bool RequireZeroTrust { get; init; } = true;

    /// <summary>
    /// Maximum number of participants per room.
    /// </summary>
    public int MaxParticipants { get; init; } = 100;

    /// <summary>
    /// Maximum number of datasets per room.
    /// </summary>
    public int MaxDatasets { get; init; } = 1000;

    /// <summary>
    /// Maximum audit trail entries per room (oldest evicted when exceeded).
    /// </summary>
    public int MaxAuditEntries { get; init; } = 100_000;
}
