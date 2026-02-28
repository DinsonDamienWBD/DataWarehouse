using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Contracts.Transit
{
    /// <summary>
    /// Defines the types of events that can be audited during data transit operations.
    /// Used by the transit audit service to categorize and track transfer lifecycle events.
    /// </summary>
    public enum TransitAuditEventType
    {
        /// <summary>
        /// A data transfer operation has been initiated.
        /// </summary>
        TransferStarted = 0,

        /// <summary>
        /// A data transfer operation completed successfully.
        /// </summary>
        TransferCompleted = 1,

        /// <summary>
        /// A data transfer operation failed due to an error.
        /// </summary>
        TransferFailed = 2,

        /// <summary>
        /// An interrupted data transfer was resumed from a checkpoint.
        /// </summary>
        TransferResumed = 3,

        /// <summary>
        /// A data transfer was cancelled by user request or policy.
        /// </summary>
        TransferCancelled = 4,

        /// <summary>
        /// A transit strategy was selected by the orchestrator for a transfer.
        /// </summary>
        StrategySelected = 5,

        /// <summary>
        /// A decorator layer (compression, encryption) was applied to a transfer.
        /// </summary>
        LayerApplied = 6,

        /// <summary>
        /// Quality of Service throttling was enforced on a transfer.
        /// </summary>
        QoSEnforced = 7,

        /// <summary>
        /// A cost-aware routing decision selected a specific route for a transfer.
        /// </summary>
        CostRouteSelected = 8
    }

    /// <summary>
    /// Represents a single audit entry for a data transit operation.
    /// Captures all relevant details about a transfer event including timing,
    /// strategy used, endpoints, and outcome information.
    /// </summary>
    /// <remarks>
    /// Audit entries are published to the message bus on the transit.audit.entry topic
    /// for consumption by compliance, monitoring, and analytics plugins.
    /// </remarks>
    public sealed record TransitAuditEntry
    {
        /// <summary>
        /// Unique identifier for this audit entry.
        /// Must be explicitly set by the creator to preserve record equality semantics.
        /// </summary>
        public required string AuditId { get; init; }

        /// <summary>
        /// The unique identifier of the transfer this audit entry relates to.
        /// </summary>
        public required string TransferId { get; init; }

        /// <summary>
        /// The type of audit event recorded.
        /// </summary>
        public required TransitAuditEventType EventType { get; init; }

        /// <summary>
        /// UTC timestamp when the event occurred. Must be explicitly set.
        /// </summary>
        public required DateTime Timestamp { get; init; }

        /// <summary>
        /// The identifier of the transit strategy involved in this event.
        /// </summary>
        public required string StrategyId { get; init; }

        /// <summary>
        /// The source endpoint URI for the transfer.
        /// </summary>
        public string SourceEndpoint { get; init; } = string.Empty;

        /// <summary>
        /// The destination endpoint URI for the transfer.
        /// </summary>
        public string DestinationEndpoint { get; init; } = string.Empty;

        /// <summary>
        /// Total number of bytes transferred at the time of this event.
        /// </summary>
        public long BytesTransferred { get; init; }

        /// <summary>
        /// Duration of the transfer operation at the time of this event.
        /// </summary>
        public TimeSpan Duration { get; init; }

        /// <summary>
        /// Indicates whether the operation was successful at the time of this event.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Error message if the operation failed. Null on success.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// The user or service identity that initiated or is associated with the transfer.
        /// </summary>
        public string? UserId { get; init; }

        /// <summary>
        /// Additional structured details about the event such as layers applied,
        /// cost information, QoS tier, compression ratio, and other contextual data.
        /// </summary>
        public Dictionary<string, object>? Details { get; init; }
    }
}
