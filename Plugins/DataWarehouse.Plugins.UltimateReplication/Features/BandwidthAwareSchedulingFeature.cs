using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateReplication.Features
{
    /// <summary>
    /// Bandwidth-Aware Scheduling Feature (C3).
    /// Schedules replication operations based on available network bandwidth,
    /// deferring large transfers to low-traffic windows and prioritizing critical data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Monitors bandwidth utilization via periodic sampling and schedules replication windows:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Immediate</b>: Small/critical transfers when bandwidth is available (&lt;70% utilization)</item>
    ///   <item><b>Deferred</b>: Large transfers queued for low-traffic windows (&lt;30% utilization)</item>
    ///   <item><b>Throttled</b>: Active transfers throttled when bandwidth exceeds threshold</item>
    /// </list>
    /// </remarks>
    public sealed class BandwidthAwareSchedulingFeature : IDisposable
    {
        private readonly ReplicationStrategyRegistry _registry;
        private readonly IMessageBus _messageBus;
        private readonly ConcurrentDictionary<string, BandwidthSample> _linkBandwidth = new();
        private readonly ConcurrentQueue<ScheduledTransfer> _deferredQueue = new();
        private readonly ConcurrentDictionary<string, ScheduledTransfer> _activeTransfers = new();
        private readonly double _highUtilizationThreshold;
        private readonly double _lowUtilizationThreshold;
        private readonly long _largeTransferThresholdBytes;
        private bool _disposed;
        private IDisposable? _bandwidthSubscription;

        // Topics
        private const string BandwidthReportTopic = "replication.ultimate.bandwidth.report";
        private const string ScheduleTransferTopic = "replication.ultimate.schedule.transfer";
        private const string TransferCompleteTopic = "replication.ultimate.transfer.complete";

        // Statistics
        private long _totalScheduled;
        private long _totalDeferred;
        private long _totalImmediate;
        private long _totalThrottled;
        private long _totalBytesScheduled;

        /// <summary>
        /// Initializes a new instance of the BandwidthAwareSchedulingFeature.
        /// </summary>
        /// <param name="registry">The replication strategy registry.</param>
        /// <param name="messageBus">Message bus for communication.</param>
        /// <param name="highUtilizationThreshold">Bandwidth threshold (0.0-1.0) above which transfers are deferred. Default: 0.7.</param>
        /// <param name="lowUtilizationThreshold">Bandwidth threshold (0.0-1.0) below which deferred transfers execute. Default: 0.3.</param>
        /// <param name="largeTransferThresholdBytes">Byte size above which transfers are considered "large". Default: 10MB.</param>
        public BandwidthAwareSchedulingFeature(
            ReplicationStrategyRegistry registry,
            IMessageBus messageBus,
            double highUtilizationThreshold = 0.7,
            double lowUtilizationThreshold = 0.3,
            long largeTransferThresholdBytes = 10 * 1024 * 1024)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
            _highUtilizationThreshold = highUtilizationThreshold;
            _lowUtilizationThreshold = lowUtilizationThreshold;
            _largeTransferThresholdBytes = largeTransferThresholdBytes;

            _bandwidthSubscription = _messageBus.Subscribe(BandwidthReportTopic, HandleBandwidthReportAsync);
        }

        /// <summary>Gets total transfers scheduled.</summary>
        public long TotalScheduled => Interlocked.Read(ref _totalScheduled);

        /// <summary>Gets total deferred transfers.</summary>
        public long TotalDeferred => Interlocked.Read(ref _totalDeferred);

        /// <summary>Gets total immediate transfers.</summary>
        public long TotalImmediate => Interlocked.Read(ref _totalImmediate);

        /// <summary>
        /// Schedules a replication transfer based on current bandwidth availability.
        /// </summary>
        /// <param name="transferId">Unique transfer identifier.</param>
        /// <param name="sourceNode">Source node ID.</param>
        /// <param name="targetNode">Target node ID.</param>
        /// <param name="dataSizeBytes">Size of data to transfer.</param>
        /// <param name="priority">Transfer priority.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Scheduling decision with estimated start time.</returns>
        public async Task<SchedulingDecision> ScheduleTransferAsync(
            string transferId,
            string sourceNode,
            string targetNode,
            long dataSizeBytes,
            TransferPriority priority = TransferPriority.Normal,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _totalScheduled);
            Interlocked.Add(ref _totalBytesScheduled, dataSizeBytes);

            var linkKey = $"{sourceNode}->{targetNode}";
            var currentUtilization = GetCurrentUtilization(linkKey);
            var isLargeTransfer = dataSizeBytes >= _largeTransferThresholdBytes;

            var transfer = new ScheduledTransfer
            {
                TransferId = transferId,
                SourceNode = sourceNode,
                TargetNode = targetNode,
                DataSizeBytes = dataSizeBytes,
                Priority = priority,
                ScheduledAt = DateTimeOffset.UtcNow,
                LinkKey = linkKey
            };

            // Critical priority always goes immediately
            if (priority == TransferPriority.Critical)
            {
                Interlocked.Increment(ref _totalImmediate);
                return await ScheduleImmediateAsync(transfer, ct);
            }

            // High utilization: defer large transfers, throttle small ones
            if (currentUtilization > _highUtilizationThreshold)
            {
                if (isLargeTransfer || priority == TransferPriority.Background)
                {
                    Interlocked.Increment(ref _totalDeferred);
                    return DeferTransfer(transfer, currentUtilization);
                }
                else
                {
                    Interlocked.Increment(ref _totalThrottled);
                    return await ScheduleThrottledAsync(transfer, currentUtilization, ct);
                }
            }

            // Low utilization: drain deferred queue first, then schedule
            if (currentUtilization < _lowUtilizationThreshold)
            {
                await DrainDeferredQueueAsync(linkKey, ct);
            }

            Interlocked.Increment(ref _totalImmediate);
            return await ScheduleImmediateAsync(transfer, ct);
        }

        /// <summary>
        /// Reports bandwidth utilization for a network link.
        /// </summary>
        /// <param name="sourceNode">Source node.</param>
        /// <param name="targetNode">Target node.</param>
        /// <param name="utilizationPercent">Current utilization (0.0-1.0).</param>
        /// <param name="availableBandwidthMbps">Available bandwidth in Mbps.</param>
        public void ReportBandwidth(string sourceNode, string targetNode, double utilizationPercent, double availableBandwidthMbps)
        {
            var linkKey = $"{sourceNode}->{targetNode}";
            _linkBandwidth[linkKey] = new BandwidthSample
            {
                LinkKey = linkKey,
                Utilization = Math.Clamp(utilizationPercent, 0.0, 1.0),
                AvailableBandwidthMbps = availableBandwidthMbps,
                SampledAt = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Gets the count of deferred transfers awaiting low-traffic windows.
        /// </summary>
        public int DeferredQueueDepth => _deferredQueue.Count;

        /// <summary>
        /// Gets bandwidth statistics for all monitored links.
        /// </summary>
        public IReadOnlyDictionary<string, BandwidthSample> GetBandwidthStats()
        {
            return _linkBandwidth;
        }

        #region Private Methods

        private double GetCurrentUtilization(string linkKey)
        {
            if (_linkBandwidth.TryGetValue(linkKey, out var sample))
            {
                // Only use recent samples (within 60 seconds)
                if (DateTimeOffset.UtcNow - sample.SampledAt < TimeSpan.FromSeconds(60))
                    return sample.Utilization;
            }
            return 0.5; // Unknown bandwidth, assume moderate utilization
        }

        private async Task<SchedulingDecision> ScheduleImmediateAsync(ScheduledTransfer transfer, CancellationToken ct)
        {
            _activeTransfers[transfer.TransferId] = transfer;

            await _messageBus.PublishAsync(ScheduleTransferTopic, new PluginMessage
            {
                Type = ScheduleTransferTopic,
                CorrelationId = transfer.TransferId,
                Source = "replication.ultimate.scheduler",
                Payload = new Dictionary<string, object>
                {
                    ["transferId"] = transfer.TransferId,
                    ["sourceNode"] = transfer.SourceNode,
                    ["targetNode"] = transfer.TargetNode,
                    ["dataSizeBytes"] = transfer.DataSizeBytes,
                    ["priority"] = transfer.Priority.ToString(),
                    ["mode"] = "immediate"
                }
            }, ct);

            return new SchedulingDecision
            {
                TransferId = transfer.TransferId,
                Mode = SchedulingMode.Immediate,
                EstimatedStartTime = DateTimeOffset.UtcNow,
                EstimatedDurationMs = EstimateTransferDuration(transfer),
                Reason = "Bandwidth available for immediate transfer"
            };
        }

        private async Task<SchedulingDecision> ScheduleThrottledAsync(
            ScheduledTransfer transfer, double currentUtilization, CancellationToken ct)
        {
            var throttleRatio = 1.0 - (currentUtilization - _highUtilizationThreshold) / (1.0 - _highUtilizationThreshold);
            throttleRatio = Math.Clamp(throttleRatio, 0.1, 1.0);

            _activeTransfers[transfer.TransferId] = transfer;

            await _messageBus.PublishAsync(ScheduleTransferTopic, new PluginMessage
            {
                Type = ScheduleTransferTopic,
                CorrelationId = transfer.TransferId,
                Source = "replication.ultimate.scheduler",
                Payload = new Dictionary<string, object>
                {
                    ["transferId"] = transfer.TransferId,
                    ["sourceNode"] = transfer.SourceNode,
                    ["targetNode"] = transfer.TargetNode,
                    ["dataSizeBytes"] = transfer.DataSizeBytes,
                    ["priority"] = transfer.Priority.ToString(),
                    ["mode"] = "throttled",
                    ["throttleRatio"] = throttleRatio
                }
            }, ct);

            var estimatedMs = EstimateTransferDuration(transfer) / throttleRatio;

            return new SchedulingDecision
            {
                TransferId = transfer.TransferId,
                Mode = SchedulingMode.Throttled,
                EstimatedStartTime = DateTimeOffset.UtcNow,
                EstimatedDurationMs = (long)estimatedMs,
                ThrottleRatio = throttleRatio,
                Reason = $"Bandwidth at {currentUtilization:P0}, throttled to {throttleRatio:P0} capacity"
            };
        }

        private SchedulingDecision DeferTransfer(ScheduledTransfer transfer, double currentUtilization)
        {
            _deferredQueue.Enqueue(transfer);

            return new SchedulingDecision
            {
                TransferId = transfer.TransferId,
                Mode = SchedulingMode.Deferred,
                EstimatedStartTime = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(15),
                EstimatedDurationMs = EstimateTransferDuration(transfer),
                Reason = $"Large transfer deferred. Bandwidth at {currentUtilization:P0}, waiting for <{_lowUtilizationThreshold:P0}"
            };
        }

        private async Task DrainDeferredQueueAsync(string linkKey, CancellationToken ct)
        {
            var retryQueue = new List<ScheduledTransfer>();

            while (_deferredQueue.TryDequeue(out var transfer))
            {
                if (transfer.LinkKey == linkKey)
                {
                    await ScheduleImmediateAsync(transfer, ct);
                    Interlocked.Increment(ref _totalImmediate);
                }
                else
                {
                    retryQueue.Add(transfer);
                }
            }

            foreach (var t in retryQueue)
                _deferredQueue.Enqueue(t);
        }

        private long EstimateTransferDuration(ScheduledTransfer transfer)
        {
            if (_linkBandwidth.TryGetValue(transfer.LinkKey, out var sample) && sample.AvailableBandwidthMbps > 0)
            {
                var bytesPerMs = sample.AvailableBandwidthMbps * 1024 * 1024 / 8 / 1000;
                return (long)(transfer.DataSizeBytes / bytesPerMs);
            }
            // Default: assume 100 Mbps
            return (long)(transfer.DataSizeBytes / (100.0 * 1024 * 1024 / 8 / 1000));
        }

        private Task HandleBandwidthReportAsync(PluginMessage message)
        {
            var sourceNode = message.Payload.GetValueOrDefault("sourceNode")?.ToString();
            var targetNode = message.Payload.GetValueOrDefault("targetNode")?.ToString();
            var utilization = message.Payload.GetValueOrDefault("utilization") is double u ? u : 0.5;
            var bandwidth = message.Payload.GetValueOrDefault("bandwidthMbps") is double b ? b : 100.0;

            if (!string.IsNullOrEmpty(sourceNode) && !string.IsNullOrEmpty(targetNode))
            {
                ReportBandwidth(sourceNode, targetNode, utilization, bandwidth);
            }

            return Task.CompletedTask;
        }

        #endregion

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _bandwidthSubscription?.Dispose();
        }
    }

    #region Bandwidth Types

    /// <summary>
    /// Transfer priority levels.
    /// </summary>
    public enum TransferPriority
    {
        /// <summary>Critical: always transferred immediately.</summary>
        Critical,
        /// <summary>High: immediate unless severely bandwidth-constrained.</summary>
        High,
        /// <summary>Normal: standard scheduling rules apply.</summary>
        Normal,
        /// <summary>Low: deferred when bandwidth is constrained.</summary>
        Low,
        /// <summary>Background: only transferred during low-traffic windows.</summary>
        Background
    }

    /// <summary>
    /// Scheduling mode applied to a transfer.
    /// </summary>
    public enum SchedulingMode
    {
        /// <summary>Transfer starts immediately at full speed.</summary>
        Immediate,
        /// <summary>Transfer starts immediately but at reduced throughput.</summary>
        Throttled,
        /// <summary>Transfer deferred to a low-traffic window.</summary>
        Deferred
    }

    /// <summary>
    /// Bandwidth measurement sample.
    /// </summary>
    public sealed class BandwidthSample
    {
        /// <summary>Network link key (source->target).</summary>
        public required string LinkKey { get; init; }
        /// <summary>Current utilization (0.0-1.0).</summary>
        public required double Utilization { get; init; }
        /// <summary>Available bandwidth in Mbps.</summary>
        public required double AvailableBandwidthMbps { get; init; }
        /// <summary>When this sample was taken.</summary>
        public required DateTimeOffset SampledAt { get; init; }
    }

    /// <summary>
    /// Scheduled transfer record.
    /// </summary>
    public sealed class ScheduledTransfer
    {
        /// <summary>Transfer identifier.</summary>
        public required string TransferId { get; init; }
        /// <summary>Source node.</summary>
        public required string SourceNode { get; init; }
        /// <summary>Target node.</summary>
        public required string TargetNode { get; init; }
        /// <summary>Data size in bytes.</summary>
        public required long DataSizeBytes { get; init; }
        /// <summary>Transfer priority.</summary>
        public required TransferPriority Priority { get; init; }
        /// <summary>When the transfer was scheduled.</summary>
        public required DateTimeOffset ScheduledAt { get; init; }
        /// <summary>Network link key.</summary>
        public required string LinkKey { get; init; }
    }

    /// <summary>
    /// Scheduling decision for a transfer.
    /// </summary>
    public sealed class SchedulingDecision
    {
        /// <summary>Transfer identifier.</summary>
        public required string TransferId { get; init; }
        /// <summary>Scheduling mode applied.</summary>
        public required SchedulingMode Mode { get; init; }
        /// <summary>Estimated start time.</summary>
        public required DateTimeOffset EstimatedStartTime { get; init; }
        /// <summary>Estimated transfer duration in milliseconds.</summary>
        public required long EstimatedDurationMs { get; init; }
        /// <summary>Throttle ratio (1.0 = full speed, 0.1 = 10% speed). Only for Throttled mode.</summary>
        public double ThrottleRatio { get; init; } = 1.0;
        /// <summary>Human-readable reason for the scheduling decision.</summary>
        public required string Reason { get; init; }
    }

    #endregion
}
