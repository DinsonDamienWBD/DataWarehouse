using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Security
{
    /// <summary>
    /// Defines when and how cryptographic keys should be rotated.
    /// Implementations can use time-based, usage-based, or event-based triggers.
    /// </summary>
    public interface IKeyRotationPolicy
    {
        /// <summary>
        /// Gets the unique identifier for this rotation policy.
        /// </summary>
        string PolicyId { get; }

        /// <summary>
        /// Gets the human-readable description of this policy.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Evaluates whether a key should be rotated based on its metadata and usage.
        /// </summary>
        /// <param name="keyId">The key identifier.</param>
        /// <param name="metadata">Current key metadata (creation time, usage count, etc.).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A rotation decision with reason.</returns>
        Task<KeyRotationDecision> ShouldRotateAsync(string keyId, KeyRotationMetadata metadata, CancellationToken ct = default);

        /// <summary>
        /// Gets the rotation triggers configured for this policy.
        /// </summary>
        IReadOnlyList<KeyRotationTrigger> Triggers { get; }
    }

    /// <summary>
    /// Result of a key rotation evaluation.
    /// </summary>
    public record KeyRotationDecision
    {
        /// <summary>
        /// Whether the key should be rotated.
        /// </summary>
        public bool ShouldRotate { get; init; }

        /// <summary>
        /// Urgency level of the rotation.
        /// </summary>
        public RotationUrgency Urgency { get; init; }

        /// <summary>
        /// Human-readable reason for the decision.
        /// </summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>
        /// The trigger that caused the rotation decision, if any.
        /// </summary>
        public KeyRotationTrigger? TriggeredBy { get; init; }

        /// <summary>
        /// Creates a decision indicating no rotation is needed.
        /// </summary>
        public static KeyRotationDecision NoRotation(string reason) =>
            new() { ShouldRotate = false, Urgency = RotationUrgency.None, Reason = reason };

        /// <summary>
        /// Creates a decision indicating rotation is needed.
        /// </summary>
        public static KeyRotationDecision Rotate(RotationUrgency urgency, string reason, KeyRotationTrigger? trigger = null) =>
            new() { ShouldRotate = true, Urgency = urgency, Reason = reason, TriggeredBy = trigger };
    }

    /// <summary>
    /// Urgency level for key rotation.
    /// </summary>
    public enum RotationUrgency
    {
        /// <summary>No rotation needed.</summary>
        None,

        /// <summary>Rotation recommended at next convenient time.</summary>
        Low,

        /// <summary>Rotation should happen soon (within hours).</summary>
        Medium,

        /// <summary>Rotation required immediately (key may be compromised).</summary>
        Critical
    }

    /// <summary>
    /// A trigger condition that can initiate key rotation.
    /// </summary>
    public record KeyRotationTrigger
    {
        /// <summary>
        /// Type of trigger.
        /// </summary>
        public KeyRotationTriggerType Type { get; init; }

        /// <summary>
        /// For time-based triggers: maximum age of a key before rotation.
        /// </summary>
        public TimeSpan? MaxKeyAge { get; init; }

        /// <summary>
        /// For usage-based triggers: maximum number of operations before rotation.
        /// </summary>
        public long? MaxUsageCount { get; init; }

        /// <summary>
        /// For data-based triggers: maximum bytes encrypted before rotation.
        /// </summary>
        public long? MaxBytesProcessed { get; init; }

        /// <summary>
        /// For event-based triggers: the event name that triggers rotation.
        /// </summary>
        public string? EventName { get; init; }

        /// <summary>
        /// Creates a time-based rotation trigger.
        /// </summary>
        public static KeyRotationTrigger TimeBased(TimeSpan maxAge) =>
            new() { Type = KeyRotationTriggerType.TimeBased, MaxKeyAge = maxAge };

        /// <summary>
        /// Creates a usage-count-based rotation trigger.
        /// </summary>
        public static KeyRotationTrigger UsageBased(long maxOperations) =>
            new() { Type = KeyRotationTriggerType.UsageBased, MaxUsageCount = maxOperations };

        /// <summary>
        /// Creates a data-volume-based rotation trigger.
        /// </summary>
        public static KeyRotationTrigger DataVolumeBased(long maxBytes) =>
            new() { Type = KeyRotationTriggerType.DataVolumeBased, MaxBytesProcessed = maxBytes };

        /// <summary>
        /// Creates an event-based rotation trigger.
        /// </summary>
        public static KeyRotationTrigger EventBased(string eventName) =>
            new() { Type = KeyRotationTriggerType.EventBased, EventName = eventName };
    }

    /// <summary>
    /// Types of key rotation triggers.
    /// </summary>
    public enum KeyRotationTriggerType
    {
        /// <summary>Rotate after a fixed time period.</summary>
        TimeBased,

        /// <summary>Rotate after N operations (encryptions, signings).</summary>
        UsageBased,

        /// <summary>Rotate after N bytes processed.</summary>
        DataVolumeBased,

        /// <summary>Rotate when a specific event occurs (compromise detected, compliance audit).</summary>
        EventBased
    }

    /// <summary>
    /// Metadata about a key used for rotation decisions.
    /// </summary>
    public record KeyRotationMetadata
    {
        /// <summary>When the key was created (UTC, unambiguous in distributed systems).</summary>
        public DateTimeOffset CreatedAt { get; init; }

        /// <summary>When the key was last used (UTC).</summary>
        public DateTimeOffset? LastUsedAt { get; init; }

        /// <summary>When the key was last rotated (UTC, null if never rotated).</summary>
        public DateTimeOffset? LastRotatedAt { get; init; }

        /// <summary>Number of operations performed with this key.</summary>
        public long UsageCount { get; init; }

        /// <summary>Total bytes processed with this key.</summary>
        public long BytesProcessed { get; init; }

        /// <summary>Current key version (increments on each rotation).</summary>
        public int Version { get; init; }

        /// <summary>Algorithm the key is used with.</summary>
        public string? Algorithm { get; init; }

        /// <summary>Additional metadata from the key store.</summary>
        public IReadOnlyDictionary<string, object> Properties { get; init; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Result of a key rotation operation.
    /// </summary>
    public record KeyRotationResult
    {
        /// <summary>Whether the rotation succeeded.</summary>
        public bool Success { get; init; }

        /// <summary>The new key ID after rotation.</summary>
        public string? NewKeyId { get; init; }

        /// <summary>The old key ID that was rotated.</summary>
        public string? OldKeyId { get; init; }

        /// <summary>When the old key will be decommissioned (grace period).</summary>
        public DateTime? OldKeyExpiresAt { get; init; }

        /// <summary>Error message if rotation failed.</summary>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Key stores that support automated key rotation implement this interface.
    /// </summary>
    /// <seealso cref="IKeyStore"/>
    /// <seealso cref="IKeyRotationPolicy"/>
    public interface IKeyRotatable : IKeyStore
    {
        /// <summary>
        /// Gets or sets the active rotation policy.
        /// </summary>
        IKeyRotationPolicy? RotationPolicy { get; set; }

        /// <summary>
        /// Rotates a key, creating a new version and optionally keeping the old version
        /// accessible for a grace period.
        /// </summary>
        /// <param name="keyId">The key to rotate.</param>
        /// <param name="gracePeriod">How long the old key remains accessible.</param>
        /// <param name="context">Security context for authorization.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Result of the rotation operation.</returns>
        Task<KeyRotationResult> RotateKeyAsync(string keyId, TimeSpan gracePeriod, ISecurityContext context, CancellationToken ct = default);

        /// <summary>
        /// Gets the rotation metadata for a specific key.
        /// </summary>
        Task<KeyRotationMetadata> GetRotationMetadataAsync(string keyId, ISecurityContext context, CancellationToken ct = default);

        /// <summary>
        /// Checks all keys against the rotation policy and returns those needing rotation.
        /// </summary>
        Task<IReadOnlyList<KeyRotationDecision>> EvaluateAllKeysAsync(ISecurityContext context, CancellationToken ct = default);
    }
}
