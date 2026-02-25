namespace DataWarehouse.Plugins.UltimateDataTransit;

/// <summary>
/// Defines standard message bus topics for the UltimateDataTransit plugin.
/// All transit-related events are published on these topics for inter-plugin communication.
/// </summary>
internal static class TransitMessageTopics
{
    /// <summary>
    /// Published when a data transfer operation begins.
    /// Payload includes TransferId, StrategyId, Source, Destination.
    /// </summary>
    public const string TransferStarted = "transit.transfer.started";

    /// <summary>
    /// Published periodically during a transfer to report progress.
    /// Payload includes TransferId, BytesTransferred, TotalBytes, PercentComplete.
    /// </summary>
    public const string TransferProgress = "transit.transfer.progress";

    /// <summary>
    /// Published when a data transfer completes successfully.
    /// Payload includes TransferId, BytesTransferred, Duration, ContentHash.
    /// </summary>
    public const string TransferCompleted = "transit.transfer.completed";

    /// <summary>
    /// Published when a data transfer fails.
    /// Payload includes TransferId, ErrorMessage, StrategyId.
    /// </summary>
    public const string TransferFailed = "transit.transfer.failed";

    /// <summary>
    /// Published when an interrupted transfer is resumed.
    /// Payload includes TransferId, ResumeOffset.
    /// </summary>
    public const string TransferResumed = "transit.transfer.resumed";

    /// <summary>
    /// Published when a transfer is cancelled by request.
    /// Payload includes TransferId, CancelledBy.
    /// </summary>
    public const string TransferCancelled = "transit.transfer.cancelled";

    /// <summary>
    /// Published when a new strategy is registered with the orchestrator.
    /// Payload includes StrategyId, Name, Protocols.
    /// </summary>
    public const string StrategyRegistered = "transit.strategy.registered";

    /// <summary>
    /// Published when a strategy health check completes.
    /// Payload includes StrategyId, IsHealthy, ErrorMessage.
    /// </summary>
    public const string StrategyHealth = "transit.strategy.health";

    /// <summary>
    /// Published when QoS policy changes for a transfer or globally.
    /// Payload includes TransferId (optional), NewPolicy.
    /// </summary>
    public const string QoSChanged = "transit.qos.changed";

    /// <summary>
    /// Published when cost-aware routing selects a different route.
    /// Payload includes TransferId, OldStrategy, NewStrategy, CostDelta.
    /// </summary>
    public const string CostRouteChanged = "transit.cost.route.changed";

    /// <summary>
    /// Published for audit trail entries on transfer operations.
    /// Payload includes TransferId, Action, Timestamp, UserId.
    /// </summary>
    public const string AuditEntry = "transit.audit.entry";
}
