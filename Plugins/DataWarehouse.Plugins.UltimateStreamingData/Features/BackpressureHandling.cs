using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStreamingData.Features;

#region Backpressure Types

/// <summary>
/// Backpressure strategy when the downstream consumer lags.
/// </summary>
public enum BackpressureMode
{
    /// <summary>Block the producer until the consumer catches up.</summary>
    Block,

    /// <summary>Drop events when the buffer is full (newest events dropped).</summary>
    DropNewest,

    /// <summary>Drop the oldest buffered events to make room for new ones.</summary>
    DropOldest,

    /// <summary>Sample events at a dynamic rate based on load.</summary>
    Sample,

    /// <summary>Dynamically throttle the producer's rate based on consumer lag.</summary>
    DynamicThrottle
}

/// <summary>
/// Configuration for backpressure handling.
/// </summary>
public sealed record BackpressureConfig
{
    /// <summary>Gets the primary backpressure mode.</summary>
    public BackpressureMode Mode { get; init; } = BackpressureMode.DynamicThrottle;

    /// <summary>Gets the maximum buffer capacity (events) before backpressure engages.</summary>
    public int BufferCapacity { get; init; } = 10_000;

    /// <summary>Gets the high watermark ratio (0-1) at which backpressure starts.</summary>
    public double HighWatermark { get; init; } = 0.8;

    /// <summary>Gets the low watermark ratio (0-1) at which backpressure releases.</summary>
    public double LowWatermark { get; init; } = 0.3;

    /// <summary>Gets the minimum producer rate (events/sec) during throttling.</summary>
    public double MinProducerRatePerSec { get; init; } = 100;

    /// <summary>Gets the maximum producer rate (events/sec) during normal operation.</summary>
    public double MaxProducerRatePerSec { get; init; } = 100_000;

    /// <summary>Gets the sampling ratio (0-1) for sample mode. 0.5 means 50% of events pass.</summary>
    public double SamplingRatio { get; init; } = 0.5;

    /// <summary>Gets the rate adjustment interval for dynamic throttling.</summary>
    public TimeSpan RateAdjustmentInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets the metrics reporting interval.</summary>
    public TimeSpan MetricsInterval { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Current backpressure status and metrics.
/// </summary>
public sealed record BackpressureStatus
{
    /// <summary>Gets the channel identifier.</summary>
    public required string ChannelId { get; init; }

    /// <summary>Gets whether backpressure is currently active.</summary>
    public bool IsBackpressureActive { get; init; }

    /// <summary>Gets the current buffer utilization ratio (0-1).</summary>
    public double BufferUtilization { get; init; }

    /// <summary>Gets the current event count in the buffer.</summary>
    public int BufferedEvents { get; init; }

    /// <summary>Gets the buffer capacity.</summary>
    public int BufferCapacity { get; init; }

    /// <summary>Gets the current producer rate (events/sec).</summary>
    public double CurrentProducerRate { get; init; }

    /// <summary>Gets the current consumer rate (events/sec).</summary>
    public double CurrentConsumerRate { get; init; }

    /// <summary>Gets the total events accepted.</summary>
    public long TotalAccepted { get; init; }

    /// <summary>Gets the total events dropped due to backpressure.</summary>
    public long TotalDropped { get; init; }

    /// <summary>Gets the total events throttled (delayed).</summary>
    public long TotalThrottled { get; init; }

    /// <summary>Gets the active backpressure mode.</summary>
    public BackpressureMode ActiveMode { get; init; }
}

#endregion

/// <summary>
/// Backpressure handling engine for flow control between stream producers and consumers.
/// Supports blocking, drop-newest, drop-oldest, sampling, and dynamic throttling modes
/// with configurable high/low watermarks and rate-based adaptive flow control.
/// </summary>
/// <remarks>
/// <b>MESSAGE BUS:</b> Publishes backpressure events to "streaming.backpressure.status" topic.
/// Uses System.Threading.Channels for bounded producer-consumer patterns.
/// Thread-safe for concurrent producers and consumers.
/// </remarks>
internal sealed class BackpressureHandling : IDisposable
{
    private readonly ConcurrentDictionary<string, BackpressureChannel> _channels = new();
    private readonly BackpressureConfig _config;
    private readonly IMessageBus? _messageBus;
    private readonly Timer _rateAdjustmentTimer;
    private readonly Timer _metricsTimer;
    private long _globalAccepted;
    private long _globalDropped;
    private long _globalThrottled;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackpressureHandling"/> class.
    /// </summary>
    /// <param name="config">Backpressure configuration.</param>
    /// <param name="messageBus">Optional message bus for status notifications.</param>
    public BackpressureHandling(BackpressureConfig? config = null, IMessageBus? messageBus = null)
    {
        _config = config ?? new BackpressureConfig();
        _messageBus = messageBus;

        _rateAdjustmentTimer = new Timer(
            AdjustRates,
            null,
            _config.RateAdjustmentInterval,
            _config.RateAdjustmentInterval);

        _metricsTimer = new Timer(
            async _ => await ReportMetricsAsync(CancellationToken.None),
            null,
            _config.MetricsInterval,
            _config.MetricsInterval);
    }

    /// <summary>Gets the total events accepted globally.</summary>
    public long GlobalAccepted => Interlocked.Read(ref _globalAccepted);

    /// <summary>Gets the total events dropped globally.</summary>
    public long GlobalDropped => Interlocked.Read(ref _globalDropped);

    /// <summary>Gets the total events throttled globally.</summary>
    public long GlobalThrottled => Interlocked.Read(ref _globalThrottled);

    /// <summary>
    /// Creates a backpressure-controlled channel for a stream.
    /// </summary>
    /// <param name="channelId">Unique channel identifier.</param>
    /// <param name="config">Optional per-channel configuration override.</param>
    /// <returns>The channel identifier.</returns>
    public string CreateChannel(string channelId, BackpressureConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(channelId);

        var channelConfig = config ?? _config;
        var options = new BoundedChannelOptions(channelConfig.BufferCapacity)
        {
            FullMode = channelConfig.Mode switch
            {
                BackpressureMode.Block => BoundedChannelFullMode.Wait,
                BackpressureMode.DropNewest => BoundedChannelFullMode.DropNewest,
                BackpressureMode.DropOldest => BoundedChannelFullMode.DropOldest,
                _ => BoundedChannelFullMode.Wait
            },
            SingleReader = false,
            SingleWriter = false
        };

        var channel = Channel.CreateBounded<StreamEvent>(options);
        var bpChannel = new BackpressureChannel(channel, channelConfig);
        _channels[channelId] = bpChannel;

        return channelId;
    }

    /// <summary>
    /// Submits an event to a backpressure-controlled channel.
    /// Applies the configured backpressure strategy when the buffer is full.
    /// </summary>
    /// <param name="channelId">The channel identifier.</param>
    /// <param name="evt">The event to submit.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the event was accepted; false if dropped.</returns>
    public async Task<bool> SubmitAsync(string channelId, StreamEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(channelId);
        ArgumentNullException.ThrowIfNull(evt);

        if (!_channels.TryGetValue(channelId, out var bpChannel))
            throw new InvalidOperationException($"Channel '{channelId}' not found. Create it first.");

        var config = bpChannel.Config;

        // Check sampling mode
        if (config.Mode == BackpressureMode.Sample)
        {
            if (Random.Shared.NextDouble() > config.SamplingRatio)
            {
                Interlocked.Increment(ref _globalDropped);
                Interlocked.Increment(ref bpChannel.Dropped);
                return false;
            }
        }

        // Dynamic throttle: apply rate limiting
        if (config.Mode == BackpressureMode.DynamicThrottle)
        {
            var utilization = GetBufferUtilization(bpChannel);
            if (utilization >= config.HighWatermark)
            {
                bpChannel.IsBackpressureActive = true;
                var delay = CalculateThrottleDelay(bpChannel, utilization);
                if (delay > TimeSpan.Zero)
                {
                    Interlocked.Increment(ref _globalThrottled);
                    Interlocked.Increment(ref bpChannel.Throttled);
                    await Task.Delay(delay, ct);
                }
            }
            else if (utilization <= config.LowWatermark)
            {
                bpChannel.IsBackpressureActive = false;
            }
        }

        // Try to write to the channel
        try
        {
            if (config.Mode == BackpressureMode.Block)
            {
                await bpChannel.Channel.Writer.WriteAsync(evt, ct);
            }
            else
            {
                if (!bpChannel.Channel.Writer.TryWrite(evt))
                {
                    Interlocked.Increment(ref _globalDropped);
                    Interlocked.Increment(ref bpChannel.Dropped);
                    return false;
                }
            }

            Interlocked.Increment(ref _globalAccepted);
            Interlocked.Increment(ref bpChannel.Accepted);
            bpChannel.RecordProducerEvent();
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Consumes events from a backpressure-controlled channel.
    /// </summary>
    /// <param name="channelId">The channel identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of events.</returns>
    public async IAsyncEnumerable<StreamEvent> ConsumeAsync(
        string channelId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_channels.TryGetValue(channelId, out var bpChannel))
            throw new InvalidOperationException($"Channel '{channelId}' not found.");

        await foreach (var evt in bpChannel.Channel.Reader.ReadAllAsync(ct))
        {
            bpChannel.RecordConsumerEvent();
            yield return evt;
        }
    }

    /// <summary>
    /// Gets the current backpressure status for a channel.
    /// </summary>
    /// <param name="channelId">The channel identifier.</param>
    /// <returns>The backpressure status, or null if channel not found.</returns>
    public BackpressureStatus? GetStatus(string channelId)
    {
        if (!_channels.TryGetValue(channelId, out var bpChannel))
            return null;

        var buffered = bpChannel.Channel.Reader.CanCount
            ? bpChannel.Channel.Reader.Count
            : 0;

        return new BackpressureStatus
        {
            ChannelId = channelId,
            IsBackpressureActive = bpChannel.IsBackpressureActive,
            BufferUtilization = (double)buffered / bpChannel.Config.BufferCapacity,
            BufferedEvents = buffered,
            BufferCapacity = bpChannel.Config.BufferCapacity,
            CurrentProducerRate = bpChannel.ProducerRate,
            CurrentConsumerRate = bpChannel.ConsumerRate,
            TotalAccepted = Interlocked.Read(ref bpChannel.Accepted),
            TotalDropped = Interlocked.Read(ref bpChannel.Dropped),
            TotalThrottled = Interlocked.Read(ref bpChannel.Throttled),
            ActiveMode = bpChannel.Config.Mode
        };
    }

    /// <summary>
    /// Gets status for all channels.
    /// </summary>
    /// <returns>A read-only collection of channel statuses.</returns>
    public IReadOnlyCollection<BackpressureStatus> GetAllStatuses()
    {
        return _channels.Keys.Select(id => GetStatus(id)!).Where(s => s != null).ToArray();
    }

    /// <summary>
    /// Closes a channel, completing the writer and preventing new events.
    /// </summary>
    /// <param name="channelId">The channel identifier.</param>
    /// <returns>True if the channel was closed; false if not found.</returns>
    public bool CloseChannel(string channelId)
    {
        if (!_channels.TryRemove(channelId, out var bpChannel))
            return false;

        bpChannel.Channel.Writer.TryComplete();
        return true;
    }

    private static double GetBufferUtilization(BackpressureChannel bpChannel)
    {
        if (!bpChannel.Channel.Reader.CanCount)
            return 0;

        return (double)bpChannel.Channel.Reader.Count / bpChannel.Config.BufferCapacity;
    }

    private static TimeSpan CalculateThrottleDelay(BackpressureChannel bpChannel, double utilization)
    {
        // Linear interpolation between min and max delay based on utilization
        var highWm = bpChannel.Config.HighWatermark;
        var excess = Math.Max(0, utilization - highWm) / (1.0 - highWm);

        // At 100% utilization, delay = 1/MinProducerRate seconds
        // At high watermark, delay = 1/MaxProducerRate seconds
        var maxDelay = 1.0 / Math.Max(1, bpChannel.Config.MinProducerRatePerSec);
        var minDelay = 1.0 / Math.Max(1, bpChannel.Config.MaxProducerRatePerSec);

        var delaySeconds = minDelay + (maxDelay - minDelay) * excess;
        return TimeSpan.FromSeconds(Math.Max(0, delaySeconds));
    }

    private void AdjustRates(object? state)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var (_, bpChannel) in _channels)
        {
            var elapsed = (now - bpChannel.LastRateCalculation).TotalSeconds;
            if (elapsed < 0.5) continue; // Avoid division by very small intervals

            var producerEvents = Interlocked.Exchange(ref bpChannel.ProducerEventCounter, 0);
            var consumerEvents = Interlocked.Exchange(ref bpChannel.ConsumerEventCounter, 0);

            bpChannel.ProducerRate = producerEvents / elapsed;
            bpChannel.ConsumerRate = consumerEvents / elapsed;
            bpChannel.LastRateCalculation = now;
        }
    }

    private async Task ReportMetricsAsync(CancellationToken ct)
    {
        if (_messageBus == null) return;

        foreach (var (channelId, bpChannel) in _channels)
        {
            var status = GetStatus(channelId);
            if (status == null) continue;

            var message = new PluginMessage
            {
                Type = "backpressure.status",
                SourcePluginId = "com.datawarehouse.streaming.ultimate",
                Source = "BackpressureHandling",
                Payload = new Dictionary<string, object>
                {
                    ["ChannelId"] = channelId,
                    ["IsActive"] = status.IsBackpressureActive,
                    ["BufferUtilization"] = status.BufferUtilization,
                    ["BufferedEvents"] = status.BufferedEvents,
                    ["ProducerRate"] = status.CurrentProducerRate,
                    ["ConsumerRate"] = status.CurrentConsumerRate,
                    ["TotalAccepted"] = status.TotalAccepted,
                    ["TotalDropped"] = status.TotalDropped,
                    ["TotalThrottled"] = status.TotalThrottled,
                    ["Mode"] = status.ActiveMode.ToString()
                }
            };

            try
            {
                await _messageBus.PublishAsync("streaming.backpressure.status", message, ct);
            }
            catch (Exception)
            {
                // Non-critical metrics reporting
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rateAdjustmentTimer.Dispose();
        _metricsTimer.Dispose();

        foreach (var (_, bpChannel) in _channels)
        {
            bpChannel.Channel.Writer.TryComplete();
        }
        _channels.Clear();
    }

    /// <summary>
    /// Internal state for a single backpressure-controlled channel.
    /// </summary>
    private sealed class BackpressureChannel
    {
        public readonly Channel<StreamEvent> Channel;
        public readonly BackpressureConfig Config;
        public volatile bool IsBackpressureActive;
        public long Accepted;
        public long Dropped;
        public long Throttled;
        public long ProducerEventCounter;
        public long ConsumerEventCounter;
        private long _producerRateBits;
        private long _consumerRateBits;
        public DateTimeOffset LastRateCalculation = DateTimeOffset.UtcNow;

        /// <summary>Gets or sets the producer rate using Interlocked for thread safety on double.</summary>
        public double ProducerRate
        {
            get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _producerRateBits));
            set => Interlocked.Exchange(ref _producerRateBits, BitConverter.DoubleToInt64Bits(value));
        }

        /// <summary>Gets or sets the consumer rate using Interlocked for thread safety on double.</summary>
        public double ConsumerRate
        {
            get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _consumerRateBits));
            set => Interlocked.Exchange(ref _consumerRateBits, BitConverter.DoubleToInt64Bits(value));
        }

        public BackpressureChannel(Channel<StreamEvent> channel, BackpressureConfig config)
        {
            Channel = channel;
            Config = config;
        }

        public void RecordProducerEvent() => Interlocked.Increment(ref ProducerEventCounter);
        public void RecordConsumerEvent() => Interlocked.Increment(ref ConsumerEventCounter);
    }
}
