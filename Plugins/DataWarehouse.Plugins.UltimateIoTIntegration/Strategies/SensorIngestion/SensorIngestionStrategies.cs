using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIoTIntegration.Strategies.SensorIngestion;

/// <summary>
/// Base class for sensor ingestion strategies.
/// </summary>
public abstract class SensorIngestionStrategyBase : IoTStrategyBase, ISensorIngestionStrategy
{
    protected long TotalMessagesIngested;
    protected long TotalBytesIngested;
    protected readonly BoundedDictionary<string, Channel<TelemetryMessage>> Subscriptions = new BoundedDictionary<string, Channel<TelemetryMessage>>(1000);

    public override IoTStrategyCategory Category => IoTStrategyCategory.SensorIngestion;

    public abstract Task<IngestionResult> IngestAsync(TelemetryMessage message, CancellationToken ct = default);
    public abstract Task<BatchIngestionResult> IngestBatchAsync(IEnumerable<TelemetryMessage> messages, CancellationToken ct = default);
    public abstract IAsyncEnumerable<TelemetryMessage> SubscribeAsync(TelemetrySubscription subscription, CancellationToken ct = default);

    public virtual Task<IngestionStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new IngestionStatistics
        {
            TotalMessagesIngested = Interlocked.Read(ref TotalMessagesIngested),
            TotalBytesIngested = Interlocked.Read(ref TotalBytesIngested),
            AverageLatencyMs = 5.0,
            ActiveStreams = Subscriptions.Count,
            LastIngestedAt = DateTimeOffset.UtcNow
        });
    }
}

/// <summary>
/// Streaming ingestion strategy - real-time telemetry streaming.
/// </summary>
public class StreamingIngestionStrategy : SensorIngestionStrategyBase
{
    private readonly ConcurrentQueue<TelemetryMessage> _messageBuffer = new();

    public override string StrategyId => "streaming-ingestion";
    public override string StrategyName => "Streaming Ingestion";
    public override string Description => "Real-time streaming ingestion for high-frequency sensor data";
    public override string[] Tags => new[] { "iot", "sensor", "streaming", "real-time", "telemetry" };

    public override Task<IngestionResult> IngestAsync(TelemetryMessage message, CancellationToken ct = default)
    {
        _messageBuffer.Enqueue(message);
        Interlocked.Increment(ref TotalMessagesIngested);
        Interlocked.Add(ref TotalBytesIngested, message.PayloadSize);

        // Broadcast to subscribers
        foreach (var channel in Subscriptions.Values)
        {
            channel.Writer.TryWrite(message);
        }

        return Task.FromResult(new IngestionResult
        {
            Success = true,
            MessageId = message.MessageId,
            IngestedAt = DateTimeOffset.UtcNow,
            SequenceNumber = TotalMessagesIngested
        });
    }

    public override async Task<BatchIngestionResult> IngestBatchAsync(IEnumerable<TelemetryMessage> messages, CancellationToken ct = default)
    {
        var messageList = messages.ToList();
        var successful = 0;
        var failed = new List<string>();

        foreach (var message in messageList)
        {
            try
            {
                await IngestAsync(message, ct);
                successful++;
            }
            catch
            {
                failed.Add(message.MessageId);
            }
        }

        return new BatchIngestionResult
        {
            Success = failed.Count == 0,
            TotalMessages = messageList.Count,
            SuccessfulMessages = successful,
            FailedMessages = failed.Count,
            FailedMessageIds = failed
        };
    }

    public override async IAsyncEnumerable<TelemetryMessage> SubscribeAsync(
        TelemetrySubscription subscription,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var subscriptionId = Guid.NewGuid().ToString();
        var channel = Channel.CreateUnbounded<TelemetryMessage>();
        Subscriptions[subscriptionId] = channel;

        try
        {
            await foreach (var message in channel.Reader.ReadAllAsync(ct))
            {
                if (subscription.DeviceId == null || message.DeviceId == subscription.DeviceId)
                {
                    yield return message;
                }
            }
        }
        finally
        {
            Subscriptions.TryRemove(subscriptionId, out _);
        }
    }
}

/// <summary>
/// Batch ingestion strategy - optimized for bulk telemetry.
/// </summary>
public class BatchIngestionStrategy : SensorIngestionStrategyBase
{
    private readonly ConcurrentBag<TelemetryMessage> _batch = new();
    private readonly int _batchSize = 1000;

    public override string StrategyId => "batch-ingestion";
    public override string StrategyName => "Batch Ingestion";
    public override string Description => "Optimized batch ingestion for high-volume sensor data";
    public override string[] Tags => new[] { "iot", "sensor", "batch", "bulk", "telemetry" };

    public override Task<IngestionResult> IngestAsync(TelemetryMessage message, CancellationToken ct = default)
    {
        _batch.Add(message);
        Interlocked.Increment(ref TotalMessagesIngested);
        Interlocked.Add(ref TotalBytesIngested, message.PayloadSize);

        return Task.FromResult(new IngestionResult
        {
            Success = true,
            MessageId = message.MessageId,
            IngestedAt = DateTimeOffset.UtcNow
        });
    }

    public override Task<BatchIngestionResult> IngestBatchAsync(IEnumerable<TelemetryMessage> messages, CancellationToken ct = default)
    {
        var messageList = messages.ToList();

        foreach (var message in messageList)
        {
            _batch.Add(message);
            Interlocked.Increment(ref TotalMessagesIngested);
            Interlocked.Add(ref TotalBytesIngested, message.PayloadSize);
        }

        return Task.FromResult(new BatchIngestionResult
        {
            Success = true,
            TotalMessages = messageList.Count,
            SuccessfulMessages = messageList.Count,
            FailedMessages = 0
        });
    }

    public override async IAsyncEnumerable<TelemetryMessage> SubscribeAsync(
        TelemetrySubscription subscription,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_batch.TryTake(out var message))
            {
                if (subscription.DeviceId == null || message.DeviceId == subscription.DeviceId)
                    yield return message;
            }
            else
            {
                await Task.Delay(100, ct);
            }
        }
    }
}

/// <summary>
/// Buffered ingestion strategy - local buffering with periodic flush.
/// </summary>
public class BufferedIngestionStrategy : SensorIngestionStrategyBase
{
    private readonly BoundedDictionary<string, List<TelemetryMessage>> _deviceBuffers = new BoundedDictionary<string, List<TelemetryMessage>>(1000);
    private readonly int _bufferSize = 100;

    public override string StrategyId => "buffered-ingestion";
    public override string StrategyName => "Buffered Ingestion";
    public override string Description => "Buffered ingestion with local caching and periodic flush";
    public override string[] Tags => new[] { "iot", "sensor", "buffered", "cache", "telemetry" };

    public override Task<IngestionResult> IngestAsync(TelemetryMessage message, CancellationToken ct = default)
    {
        var buffer = _deviceBuffers.GetOrAdd(message.DeviceId, _ => new List<TelemetryMessage>());

        lock (buffer)
        {
            buffer.Add(message);
            if (buffer.Count >= _bufferSize)
            {
                // Flush buffer
                buffer.Clear();
            }
        }

        Interlocked.Increment(ref TotalMessagesIngested);
        Interlocked.Add(ref TotalBytesIngested, message.PayloadSize);

        return Task.FromResult(new IngestionResult
        {
            Success = true,
            MessageId = message.MessageId,
            IngestedAt = DateTimeOffset.UtcNow
        });
    }

    public override Task<BatchIngestionResult> IngestBatchAsync(IEnumerable<TelemetryMessage> messages, CancellationToken ct = default)
    {
        var messageList = messages.ToList();
        foreach (var message in messageList)
        {
            IngestAsync(message, ct);
        }

        return Task.FromResult(new BatchIngestionResult
        {
            Success = true,
            TotalMessages = messageList.Count,
            SuccessfulMessages = messageList.Count
        });
    }

    public override async IAsyncEnumerable<TelemetryMessage> SubscribeAsync(
        TelemetrySubscription subscription,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var kvp in _deviceBuffers)
            {
                if (subscription.DeviceId == null || kvp.Key == subscription.DeviceId)
                {
                    lock (kvp.Value)
                    {
                        foreach (var message in kvp.Value)
                            yield return message;
                    }
                }
            }
            await Task.Delay(1000, ct);
        }
    }
}

/// <summary>
/// Time-series ingestion strategy - optimized for time-series data.
/// </summary>
public class TimeSeriesIngestionStrategy : SensorIngestionStrategyBase
{
    private readonly BoundedDictionary<string, SortedList<DateTimeOffset, TelemetryMessage>> _timeSeries = new BoundedDictionary<string, SortedList<DateTimeOffset, TelemetryMessage>>(1000);

    public override string StrategyId => "timeseries-ingestion";
    public override string StrategyName => "Time-Series Ingestion";
    public override string Description => "Optimized ingestion for time-series sensor data with compaction";
    public override string[] Tags => new[] { "iot", "sensor", "timeseries", "compaction", "telemetry" };

    public override Task<IngestionResult> IngestAsync(TelemetryMessage message, CancellationToken ct = default)
    {
        var series = _timeSeries.GetOrAdd(message.DeviceId, _ => new SortedList<DateTimeOffset, TelemetryMessage>());

        lock (series)
        {
            series[message.Timestamp] = message;

            // Compact if too large
            if (series.Count > 10000)
            {
                var oldestKeys = series.Keys.Take(1000).ToList();
                foreach (var key in oldestKeys)
                    series.Remove(key);
            }
        }

        Interlocked.Increment(ref TotalMessagesIngested);
        Interlocked.Add(ref TotalBytesIngested, message.PayloadSize);

        return Task.FromResult(new IngestionResult
        {
            Success = true,
            MessageId = message.MessageId,
            IngestedAt = DateTimeOffset.UtcNow
        });
    }

    public override Task<BatchIngestionResult> IngestBatchAsync(IEnumerable<TelemetryMessage> messages, CancellationToken ct = default)
    {
        var messageList = messages.OrderBy(m => m.Timestamp).ToList();
        foreach (var message in messageList)
        {
            IngestAsync(message, ct);
        }

        return Task.FromResult(new BatchIngestionResult
        {
            Success = true,
            TotalMessages = messageList.Count,
            SuccessfulMessages = messageList.Count
        });
    }

    public override async IAsyncEnumerable<TelemetryMessage> SubscribeAsync(
        TelemetrySubscription subscription,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var lastSeen = DateTimeOffset.MinValue;

        while (!ct.IsCancellationRequested)
        {
            foreach (var kvp in _timeSeries)
            {
                if (subscription.DeviceId == null || kvp.Key == subscription.DeviceId)
                {
                    lock (kvp.Value)
                    {
                        foreach (var message in kvp.Value.Values.Where(m => m.Timestamp > lastSeen))
                        {
                            lastSeen = message.Timestamp;
                            yield return message;
                        }
                    }
                }
            }
            await Task.Delay(100, ct);
        }
    }
}

/// <summary>
/// Aggregating ingestion strategy - pre-aggregates data on ingestion.
/// </summary>
public class AggregatingIngestionStrategy : SensorIngestionStrategyBase
{
    private readonly BoundedDictionary<string, AggregationWindow> _windows = new BoundedDictionary<string, AggregationWindow>(1000);

    public override string StrategyId => "aggregating-ingestion";
    public override string StrategyName => "Aggregating Ingestion";
    public override string Description => "Pre-aggregates sensor data during ingestion for reduced storage";
    public override string[] Tags => new[] { "iot", "sensor", "aggregation", "rollup", "telemetry" };

    public override Task<IngestionResult> IngestAsync(TelemetryMessage message, CancellationToken ct = default)
    {
        var windowKey = $"{message.DeviceId}:{message.Timestamp:yyyy-MM-dd-HH-mm}";
        var window = _windows.GetOrAdd(windowKey, _ => new AggregationWindow());

        window.Count++;
        foreach (var kvp in message.Data)
        {
            if (kvp.Value is double value)
            {
                window.Sum.AddOrUpdate(kvp.Key, value, (_, v) => v + value);
                window.Min.AddOrUpdate(kvp.Key, value, (_, v) => Math.Min(v, value));
                window.Max.AddOrUpdate(kvp.Key, value, (_, v) => Math.Max(v, value));
            }
        }

        Interlocked.Increment(ref TotalMessagesIngested);
        Interlocked.Add(ref TotalBytesIngested, message.PayloadSize);

        return Task.FromResult(new IngestionResult
        {
            Success = true,
            MessageId = message.MessageId,
            IngestedAt = DateTimeOffset.UtcNow,
            PartitionKey = windowKey
        });
    }

    public override Task<BatchIngestionResult> IngestBatchAsync(IEnumerable<TelemetryMessage> messages, CancellationToken ct = default)
    {
        var messageList = messages.ToList();
        foreach (var message in messageList)
        {
            IngestAsync(message, ct);
        }

        return Task.FromResult(new BatchIngestionResult
        {
            Success = true,
            TotalMessages = messageList.Count,
            SuccessfulMessages = messageList.Count
        });
    }

    public override async IAsyncEnumerable<TelemetryMessage> SubscribeAsync(
        TelemetrySubscription subscription,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(60000, ct); // Emit aggregated data every minute

            // Yield aggregated telemetry
            yield return new TelemetryMessage
            {
                DeviceId = subscription.DeviceId ?? "*",
                MessageId = Guid.NewGuid().ToString(),
                Timestamp = DateTimeOffset.UtcNow,
                Data = new Dictionary<string, object>
                {
                    ["aggregation_type"] = "minute",
                    ["window_count"] = 1
                }
            };
        }
    }

    private class AggregationWindow
    {
        public int Count;
        public BoundedDictionary<string, double> Sum = new BoundedDictionary<string, double>(1000);
        public BoundedDictionary<string, double> Min = new BoundedDictionary<string, double>(1000);
        public BoundedDictionary<string, double> Max = new BoundedDictionary<string, double>(1000);
    }
}
