using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Scaling;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Infrastructure.Scaling
{
    /// <summary>
    /// Configuration for message bus backpressure thresholds and behavior.
    /// All thresholds are configurable at runtime.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 88-06: Message bus backpressure configuration")]
    public sealed class MessageBusBackpressureConfig
    {
        /// <summary>
        /// Queue depth ratio (0.0-1.0) at which Warning state activates.
        /// Default: 70% of capacity.
        /// </summary>
        public double WarningThreshold { get; set; } = 0.70;

        /// <summary>
        /// Queue depth ratio (0.0-1.0) at which Critical state activates.
        /// Default: 85% of capacity.
        /// </summary>
        public double CriticalThreshold { get; set; } = 0.85;

        /// <summary>
        /// Queue depth ratio (0.0-1.0) at which Shedding state activates.
        /// Default: 95% of capacity.
        /// </summary>
        public double SheddingThreshold { get; set; } = 0.95;

        /// <summary>
        /// Delay applied to producer PublishAsync calls during Critical state.
        /// Default: 50ms. Range: 10ms-1s.
        /// </summary>
        public TimeSpan CriticalPublishDelay { get; set; } = TimeSpan.FromMilliseconds(50);

        /// <summary>
        /// Maximum aggregate queue capacity across all partitions.
        /// Used as denominator for threshold calculations.
        /// </summary>
        public long MaxAggregateCapacity { get; set; } = 100_000;

        /// <summary>
        /// Set of topic names marked as critical (not subject to load shedding).
        /// Topics with [Critical] semantics are never rejected.
        /// </summary>
        public HashSet<string> CriticalTopics { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Creates a default configuration.</summary>
        public static MessageBusBackpressureConfig Default => new();
    }

    /// <summary>
    /// Implements <see cref="IBackpressureAware"/> for the message bus subsystem.
    /// Monitors aggregate queue depth across all partitions and signals producers
    /// through a three-level escalation: Warning, Critical, and Shedding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Backpressure behavior by state:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>Normal</b>: No intervention. All publishes proceed immediately.</description></item>
    /// <item><description><b>Warning</b>: Adds <c>X-Backpressure: warning</c> header to published messages.
    /// Consumer-side: no changes.</description></item>
    /// <item><description><b>Critical</b>: PublishAsync returns a delayed Task (configurable 10ms-1s)
    /// before completing. Consumer-side: temporarily disables batching for faster drain.</description></item>
    /// <item><description><b>Shedding</b>: Rejects publishes for non-critical topics (topics without
    /// <c>[Critical]</c> attribute). Critical topics still accepted with delay.</description></item>
    /// </list>
    /// </remarks>
    [SdkCompatibility("6.0.0", Notes = "Phase 88-06: IBackpressureAware for message bus with three-level escalation")]
    public sealed class MessageBusBackpressure : IBackpressureAware
    {
        private volatile MessageBusBackpressureConfig _config;
        private volatile BackpressureState _currentState = BackpressureState.Normal;
        private volatile BackpressureStrategy _strategy = BackpressureStrategy.Adaptive;
        private long _aggregateQueueDepth;
        private long _totalWarningEvents;
        private long _totalCriticalEvents;
        private long _totalSheddingEvents;
        private long _totalShedMessages;
        private volatile bool _batchingDisabled;

        /// <summary>
        /// Raised when the backpressure state transitions between levels.
        /// </summary>
        public event Action<BackpressureStateChangedEventArgs>? OnBackpressureChanged;

        /// <summary>
        /// Initializes a new message bus backpressure monitor.
        /// </summary>
        /// <param name="config">Optional configuration. Uses defaults if null.</param>
        public MessageBusBackpressure(MessageBusBackpressureConfig? config = null)
        {
            _config = config ?? MessageBusBackpressureConfig.Default;
        }

        /// <inheritdoc />
        public BackpressureStrategy Strategy
        {
            get => _strategy;
            set => _strategy = value;
        }

        /// <inheritdoc />
        public BackpressureState CurrentState => _currentState;

        /// <summary>
        /// Gets whether consumer batching is currently disabled due to Critical backpressure state.
        /// Consumers should check this flag and process messages individually when true.
        /// </summary>
        public bool IsBatchingDisabled => _batchingDisabled;

        /// <summary>
        /// Gets current metrics for the backpressure monitor.
        /// </summary>
        public IReadOnlyDictionary<string, object> GetMetrics()
        {
            return new Dictionary<string, object>
            {
                ["backpressure.state"] = _currentState.ToString(),
                ["backpressure.strategy"] = _strategy.ToString(),
                ["backpressure.aggregateQueueDepth"] = Interlocked.Read(ref _aggregateQueueDepth),
                ["backpressure.maxCapacity"] = _config.MaxAggregateCapacity,
                ["backpressure.utilizationRatio"] = _config.MaxAggregateCapacity > 0
                    ? (double)Interlocked.Read(ref _aggregateQueueDepth) / _config.MaxAggregateCapacity
                    : 0.0,
                ["backpressure.totalWarningEvents"] = Interlocked.Read(ref _totalWarningEvents),
                ["backpressure.totalCriticalEvents"] = Interlocked.Read(ref _totalCriticalEvents),
                ["backpressure.totalSheddingEvents"] = Interlocked.Read(ref _totalSheddingEvents),
                ["backpressure.totalShedMessages"] = Interlocked.Read(ref _totalShedMessages),
                ["backpressure.batchingDisabled"] = _batchingDisabled
            };
        }

        /// <summary>
        /// Updates the aggregate queue depth and recalculates the backpressure state.
        /// Called by the <see cref="ScalableMessageBus"/> after each publish/consume cycle.
        /// </summary>
        /// <param name="newAggregateDepth">Current total queue depth across all partitions.</param>
        public void UpdateQueueDepth(long newAggregateDepth)
        {
            Interlocked.Exchange(ref _aggregateQueueDepth, newAggregateDepth);
            EvaluateState();
        }

        /// <summary>
        /// Evaluates whether a publish should proceed based on current backpressure state.
        /// Returns a Task that may be delayed (Critical) or faulted (Shedding for non-critical).
        /// </summary>
        /// <param name="topic">The topic being published to.</param>
        /// <returns>
        /// A <see cref="PublishDecision"/> indicating whether to proceed and any required delay.
        /// </returns>
        public PublishDecision EvaluatePublish(string topic)
        {
            var state = _currentState;

            switch (state)
            {
                case BackpressureState.Normal:
                    return PublishDecision.Proceed;

                case BackpressureState.Warning:
                    return PublishDecision.ProceedWithWarning;

                case BackpressureState.Critical:
                    var delay = _config.CriticalPublishDelay;
                    // Clamp to 10ms-1s range
                    if (delay < TimeSpan.FromMilliseconds(10)) delay = TimeSpan.FromMilliseconds(10);
                    if (delay > TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
                    return new PublishDecision(true, delay, false, "X-Backpressure: critical");

                case BackpressureState.Shedding:
                    var isCriticalTopic = _config.CriticalTopics.Contains(topic);
                    if (isCriticalTopic)
                    {
                        // Critical topics still accepted but with maximum delay
                        return new PublishDecision(true, TimeSpan.FromSeconds(1), false, "X-Backpressure: shedding");
                    }
                    Interlocked.Increment(ref _totalShedMessages);
                    return PublishDecision.Rejected;

                default:
                    return PublishDecision.Proceed;
            }
        }

        /// <inheritdoc />
        public Task ApplyBackpressureAsync(BackpressureContext context, CancellationToken ct = default)
        {
            Interlocked.Exchange(ref _aggregateQueueDepth, context.QueueDepth);
            EvaluateState();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Reconfigures the backpressure thresholds at runtime.
        /// Takes effect immediately on the next state evaluation.
        /// </summary>
        /// <param name="newConfig">The new configuration.</param>
        public void Reconfigure(MessageBusBackpressureConfig newConfig)
        {
            ArgumentNullException.ThrowIfNull(newConfig);
            Interlocked.Exchange(ref _config, newConfig);
            EvaluateState();
        }

        private void EvaluateState()
        {
            var config = _config;
            var depth = Interlocked.Read(ref _aggregateQueueDepth);
            var capacity = config.MaxAggregateCapacity;

            if (capacity <= 0)
            {
                TransitionTo(BackpressureState.Normal);
                return;
            }

            var ratio = (double)depth / capacity;
            BackpressureState newState;

            if (ratio >= config.SheddingThreshold)
            {
                newState = BackpressureState.Shedding;
            }
            else if (ratio >= config.CriticalThreshold)
            {
                newState = BackpressureState.Critical;
            }
            else if (ratio >= config.WarningThreshold)
            {
                newState = BackpressureState.Warning;
            }
            else
            {
                newState = BackpressureState.Normal;
            }

            TransitionTo(newState);
        }

        private void TransitionTo(BackpressureState newState)
        {
            var previous = _currentState;
            if (previous == newState) return;

            _currentState = newState;

            // Track escalation events
            switch (newState)
            {
                case BackpressureState.Warning:
                    Interlocked.Increment(ref _totalWarningEvents);
                    break;
                case BackpressureState.Critical:
                    Interlocked.Increment(ref _totalCriticalEvents);
                    _batchingDisabled = true;
                    break;
                case BackpressureState.Shedding:
                    Interlocked.Increment(ref _totalSheddingEvents);
                    _batchingDisabled = true;
                    break;
                case BackpressureState.Normal:
                    _batchingDisabled = false;
                    break;
            }

            // Re-enable batching when de-escalating from Critical
            if (newState < BackpressureState.Critical)
            {
                _batchingDisabled = false;
            }

            OnBackpressureChanged?.Invoke(new BackpressureStateChangedEventArgs(
                previous, newState, "MessageBus", DateTime.UtcNow));
        }
    }

    /// <summary>
    /// Result of a backpressure evaluation for a publish operation.
    /// Indicates whether the publish should proceed and any required delay.
    /// </summary>
    /// <param name="ShouldProceed">Whether the publish should be accepted.</param>
    /// <param name="Delay">Delay to apply before completing the publish Task. Zero for no delay.</param>
    /// <param name="IsRejected">Whether the publish was rejected due to load shedding.</param>
    /// <param name="Header">Backpressure header value to attach to the message, if any.</param>
    [SdkCompatibility("6.0.0", Notes = "Phase 88-06: Backpressure publish decision")]
    public readonly record struct PublishDecision(
        bool ShouldProceed,
        TimeSpan Delay,
        bool IsRejected,
        string? Header)
    {
        /// <summary>Normal: proceed immediately, no header.</summary>
        public static readonly PublishDecision Proceed = new(true, TimeSpan.Zero, false, null);

        /// <summary>Warning: proceed with warning header.</summary>
        public static readonly PublishDecision ProceedWithWarning = new(true, TimeSpan.Zero, false, "X-Backpressure: warning");

        /// <summary>Rejected: publish was shed.</summary>
        public static readonly PublishDecision Rejected = new(false, TimeSpan.Zero, true, "X-Backpressure: shedding");
    }
}
