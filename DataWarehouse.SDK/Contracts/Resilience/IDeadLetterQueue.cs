using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Resilience
{
    /// <summary>
    /// Contract for dead letter queue handling of failed messages (RESIL-05).
    /// Provides storage, retry, and discard capabilities for messages
    /// that could not be delivered or processed successfully.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public interface IDeadLetterQueue
    {
        /// <summary>
        /// Raised when a dead letter event occurs.
        /// </summary>
        event Action<DeadLetterEvent>? OnDeadLetterEvent;

        /// <summary>
        /// Adds a failed message to the dead letter queue.
        /// </summary>
        /// <param name="message">The dead letter message to enqueue.</param>
        /// <param name="ct">Cancellation token for the enqueue operation.</param>
        /// <returns>A task representing the enqueue operation.</returns>
        Task EnqueueAsync(DeadLetterMessage message, CancellationToken ct = default);

        /// <summary>
        /// Views messages without removing them from the queue.
        /// </summary>
        /// <param name="maxCount">Maximum number of messages to return.</param>
        /// <param name="ct">Cancellation token for the peek operation.</param>
        /// <returns>A read-only list of dead letter messages.</returns>
        Task<IReadOnlyList<DeadLetterMessage>> PeekAsync(int maxCount, CancellationToken ct = default);

        /// <summary>
        /// Removes and returns the oldest message from the queue.
        /// </summary>
        /// <param name="ct">Cancellation token for the dequeue operation.</param>
        /// <returns>The oldest dead letter message, or null if the queue is empty.</returns>
        Task<DeadLetterMessage?> DequeueAsync(CancellationToken ct = default);

        /// <summary>
        /// Re-publishes a failed message for retry.
        /// </summary>
        /// <param name="messageId">The message identifier to retry.</param>
        /// <param name="ct">Cancellation token for the retry operation.</param>
        /// <returns>True if the message was found and retried; false if not found.</returns>
        Task<bool> RetryAsync(string messageId, CancellationToken ct = default);

        /// <summary>
        /// Permanently discards a message from the queue.
        /// </summary>
        /// <param name="messageId">The message identifier to discard.</param>
        /// <param name="reason">The reason for discarding.</param>
        /// <param name="ct">Cancellation token for the discard operation.</param>
        /// <returns>True if the message was found and discarded; false if not found.</returns>
        Task<bool> DiscardAsync(string messageId, string reason, CancellationToken ct = default);

        /// <summary>
        /// Gets the number of messages currently in the queue.
        /// </summary>
        /// <param name="ct">Cancellation token for the count operation.</param>
        /// <returns>The number of messages in the queue.</returns>
        Task<int> GetCountAsync(CancellationToken ct = default);

        /// <summary>
        /// Removes all messages from the queue.
        /// </summary>
        /// <param name="ct">Cancellation token for the purge operation.</param>
        /// <returns>A task representing the purge operation.</returns>
        Task PurgeAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// A message that failed delivery and is stored in the dead letter queue.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public record DeadLetterMessage
    {
        /// <summary>
        /// Unique identifier for this dead letter message.
        /// </summary>
        public required string MessageId { get; init; }

        /// <summary>
        /// The original topic the message was published to.
        /// </summary>
        public required string OriginalTopic { get; init; }

        /// <summary>
        /// The original message that failed delivery.
        /// </summary>
        public required PluginMessage OriginalMessage { get; init; }

        /// <summary>
        /// The reason the message failed.
        /// </summary>
        public required string FailureReason { get; init; }

        /// <summary>
        /// The exception that caused the failure, if any.
        /// </summary>
        public Exception? Exception { get; init; }

        /// <summary>
        /// Number of times this message has been retried.
        /// </summary>
        public required int RetryCount { get; init; }

        /// <summary>
        /// Maximum number of retries allowed.
        /// </summary>
        public required int MaxRetries { get; init; }

        /// <summary>
        /// When the message originally failed.
        /// </summary>
        public required DateTimeOffset FailedAt { get; init; }

        /// <summary>
        /// When the next retry is scheduled, if any.
        /// </summary>
        public DateTimeOffset? NextRetryAt { get; init; }

        /// <summary>
        /// The retry policy for this message.
        /// </summary>
        public required DeadLetterRetryPolicy RetryPolicy { get; init; }
    }

    /// <summary>
    /// Retry policy for dead letter messages.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public record DeadLetterRetryPolicy
    {
        /// <summary>
        /// Maximum number of retries. Default: 3.
        /// </summary>
        public int MaxRetries { get; init; } = 3;

        /// <summary>
        /// Initial delay before the first retry. Default: 1 second.
        /// </summary>
        public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Maximum delay between retries. Default: 60 seconds.
        /// </summary>
        public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Multiplier applied to the delay for each subsequent retry. Default: 2.0.
        /// </summary>
        public double BackoffMultiplier { get; init; } = 2.0;

        /// <summary>
        /// The retry strategy to use.
        /// </summary>
        public DeadLetterRetryStrategy Strategy { get; init; } = DeadLetterRetryStrategy.ExponentialBackoff;
    }

    /// <summary>
    /// Retry strategies for dead letter messages.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public enum DeadLetterRetryStrategy
    {
        /// <summary>Fixed delay between retries.</summary>
        Fixed,
        /// <summary>Exponentially increasing delay between retries.</summary>
        ExponentialBackoff,
        /// <summary>Linearly increasing delay between retries.</summary>
        LinearBackoff
    }

    /// <summary>
    /// An event describing dead letter queue operations.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public record DeadLetterEvent
    {
        /// <summary>
        /// The type of dead letter event.
        /// </summary>
        public required DeadLetterEventType EventType { get; init; }

        /// <summary>
        /// The message identifier involved in the event.
        /// </summary>
        public required string MessageId { get; init; }

        /// <summary>
        /// The original topic of the message.
        /// </summary>
        public required string OriginalTopic { get; init; }

        /// <summary>
        /// When the event occurred.
        /// </summary>
        public required DateTimeOffset Timestamp { get; init; }

        /// <summary>
        /// Optional detail about the event.
        /// </summary>
        public string? Detail { get; init; }
    }

    /// <summary>
    /// Types of dead letter events.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public enum DeadLetterEventType
    {
        /// <summary>A message was added to the dead letter queue.</summary>
        Enqueued,
        /// <summary>A message retry was attempted.</summary>
        Retried,
        /// <summary>A message retry succeeded.</summary>
        RetrySucceeded,
        /// <summary>A message retry failed.</summary>
        RetryFailed,
        /// <summary>A message was permanently discarded.</summary>
        Discarded,
        /// <summary>All messages were purged from the queue.</summary>
        Purged
    }
}
