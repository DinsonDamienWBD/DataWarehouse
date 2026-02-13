using DataWarehouse.SDK.Contracts.Transit;

namespace DataWarehouse.Plugins.UltimateDataTransit.QoS;

/// <summary>
/// Configuration for QoS bandwidth management.
/// Defines total bandwidth and per-priority-tier allocation with minimum guarantees.
/// </summary>
internal sealed record QoSConfiguration
{
    /// <summary>
    /// Total bandwidth available in bytes per second across all priority tiers.
    /// </summary>
    public long TotalBandwidthBytesPerSecond { get; init; }

    /// <summary>
    /// Per-priority-tier bandwidth configuration.
    /// Each tier has weight, minimum guarantee, and maximum ceiling.
    /// </summary>
    public Dictionary<TransitPriority, PriorityConfig> PriorityConfigs { get; init; } = new()
    {
        [TransitPriority.Critical] = new PriorityConfig
        {
            WeightPercent = 0.50,
            MinBandwidthBytesPerSecond = 5 * 1024 * 1024, // 5 MB/s minimum
            MaxBandwidthBytesPerSecond = long.MaxValue
        },
        [TransitPriority.High] = new PriorityConfig
        {
            WeightPercent = 0.30,
            MinBandwidthBytesPerSecond = 3 * 1024 * 1024, // 3 MB/s minimum
            MaxBandwidthBytesPerSecond = long.MaxValue
        },
        [TransitPriority.Normal] = new PriorityConfig
        {
            WeightPercent = 0.15,
            MinBandwidthBytesPerSecond = 2 * 1024 * 1024, // 2 MB/s minimum
            MaxBandwidthBytesPerSecond = long.MaxValue
        },
        [TransitPriority.Low] = new PriorityConfig
        {
            WeightPercent = 0.05,
            MinBandwidthBytesPerSecond = 1 * 1024 * 1024, // 1 MB/s minimum (starvation prevention)
            MaxBandwidthBytesPerSecond = long.MaxValue
        }
    };
}

/// <summary>
/// Configuration for a single priority tier's bandwidth allocation.
/// </summary>
internal sealed record PriorityConfig
{
    /// <summary>
    /// Percentage weight of total bandwidth allocated to this priority tier (0.0 to 1.0).
    /// Used for distributing remaining bandwidth after minimum guarantees are reserved.
    /// </summary>
    public double WeightPercent { get; init; }

    /// <summary>
    /// Minimum guaranteed bandwidth in bytes per second.
    /// This floor is always reserved, ensuring no priority tier experiences starvation.
    /// </summary>
    public long MinBandwidthBytesPerSecond { get; init; }

    /// <summary>
    /// Maximum bandwidth ceiling in bytes per second.
    /// Use <see cref="long.MaxValue"/> for no ceiling.
    /// </summary>
    public long MaxBandwidthBytesPerSecond { get; init; }
}

/// <summary>
/// Manages Quality of Service bandwidth throttling using a token bucket algorithm
/// with weighted fair queueing and per-priority-tier minimum bandwidth guarantees.
/// </summary>
/// <remarks>
/// <para>
/// Bandwidth is allocated in two phases:
/// <list type="number">
/// <item><description><b>Phase 1 - Minimum guarantees:</b> Each priority tier receives its configured
/// minimum bandwidth, reserved unconditionally. This prevents starvation of lower-priority
/// transfers per research pitfall 5.</description></item>
/// <item><description><b>Phase 2 - Weighted distribution:</b> Remaining bandwidth (total minus sum of
/// minimums) is distributed across tiers by their weight percentage.</description></item>
/// </list>
/// </para>
/// <para>
/// Each priority tier has its own <see cref="TokenBucket"/> instance that enforces rate limiting.
/// Transfers consume tokens from the appropriate bucket; when insufficient tokens are available,
/// the consumer blocks until tokens refill at the configured rate.
/// </para>
/// </remarks>
internal sealed class QoSThrottlingManager : IDisposable
{
    private readonly object _configLock = new();
    private Dictionary<TransitPriority, TokenBucket> _buckets;
    private QoSConfiguration _configuration;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="QoSThrottlingManager"/> class
    /// with the specified bandwidth configuration.
    /// </summary>
    /// <param name="configuration">The QoS configuration defining bandwidth allocation.</param>
    public QoSThrottlingManager(QoSConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
        _buckets = CreateBuckets(configuration);
    }

    /// <summary>
    /// Creates a throttled stream that wraps the inner stream with bandwidth enforcement
    /// for the specified priority tier.
    /// </summary>
    /// <param name="inner">The inner stream to wrap with throttling.</param>
    /// <param name="priority">The priority tier determining bandwidth allocation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ThrottledStream"/> that enforces bandwidth limits on read/write operations.</returns>
    public Task<Stream> CreateThrottledStreamAsync(Stream inner, TransitPriority priority, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ct.ThrowIfCancellationRequested();

        TokenBucket bucket;
        lock (_configLock)
        {
            if (!_buckets.TryGetValue(priority, out bucket!))
            {
                // Fallback to Normal priority if the requested tier is not configured
                bucket = _buckets.GetValueOrDefault(TransitPriority.Normal)
                    ?? _buckets.Values.First();
            }
        }

        Stream throttledStream = new ThrottledStream(inner, bucket);
        return Task.FromResult(throttledStream);
    }

    /// <summary>
    /// Updates the bandwidth configuration at runtime, recreating token buckets with new limits.
    /// Thread-safe: acquires a lock to swap the bucket dictionary atomically.
    /// </summary>
    /// <param name="config">The new QoS configuration to apply.</param>
    public void UpdateConfiguration(QoSConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (_configLock)
        {
            var oldBuckets = _buckets;
            _configuration = config;
            _buckets = CreateBuckets(config);

            // Dispose old buckets
            foreach (var bucket in oldBuckets.Values)
            {
                bucket.Dispose();
            }
        }
    }

    /// <summary>
    /// Gets the current configuration.
    /// </summary>
    public QoSConfiguration Configuration
    {
        get
        {
            lock (_configLock)
            {
                return _configuration;
            }
        }
    }

    /// <summary>
    /// Gets the available token count for a specific priority tier (for diagnostics).
    /// </summary>
    /// <param name="priority">The priority tier to query.</param>
    /// <returns>The number of available tokens (bytes) in the bucket.</returns>
    public long GetAvailableTokens(TransitPriority priority)
    {
        lock (_configLock)
        {
            return _buckets.TryGetValue(priority, out var bucket)
                ? bucket.AvailableTokens
                : 0;
        }
    }

    /// <summary>
    /// Creates token buckets for each priority tier based on the configuration.
    /// Allocates bandwidth in two phases: minimum guarantees first, then weighted distribution.
    /// </summary>
    /// <param name="config">The QoS configuration.</param>
    /// <returns>Dictionary mapping priority tiers to their token buckets.</returns>
    private static Dictionary<TransitPriority, TokenBucket> CreateBuckets(QoSConfiguration config)
    {
        var totalBandwidth = config.TotalBandwidthBytesPerSecond;
        var buckets = new Dictionary<TransitPriority, TokenBucket>();

        // Phase 1: Reserve minimum guarantees
        long reservedBandwidth = 0;
        foreach (var kvp in config.PriorityConfigs)
        {
            reservedBandwidth += kvp.Value.MinBandwidthBytesPerSecond;
        }

        // Phase 2: Distribute remaining bandwidth by weight
        var remainingBandwidth = Math.Max(0, totalBandwidth - reservedBandwidth);

        foreach (var kvp in config.PriorityConfigs)
        {
            var priority = kvp.Key;
            var priorityConfig = kvp.Value;

            // Base = minimum guarantee + weighted share of remainder
            var weightedShare = (long)(remainingBandwidth * priorityConfig.WeightPercent);
            var allocatedBandwidth = priorityConfig.MinBandwidthBytesPerSecond + weightedShare;

            // Apply ceiling
            allocatedBandwidth = Math.Min(allocatedBandwidth, priorityConfig.MaxBandwidthBytesPerSecond);

            // Token bucket capacity = 2x the per-second rate for burst handling
            var bucketCapacity = allocatedBandwidth * 2;

            buckets[priority] = new TokenBucket(
                maxTokens: bucketCapacity,
                tokensPerSecond: allocatedBandwidth);
        }

        return buckets;
    }

    /// <summary>
    /// Releases resources used by the throttling manager and all token buckets.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_configLock)
        {
            foreach (var bucket in _buckets.Values)
            {
                bucket.Dispose();
            }
            _buckets.Clear();
        }
    }
}

/// <summary>
/// Token bucket rate limiter for bandwidth enforcement.
/// Refills tokens at a constant rate and blocks consumers when insufficient tokens are available.
/// Thread-safe for concurrent access by multiple transfer streams.
/// </summary>
internal sealed class TokenBucket : IDisposable
{
    private readonly long _maxTokens;
    private readonly long _tokensPerSecond;
    private long _availableTokens;
    private DateTime _lastRefillTime;
    private readonly SemaphoreSlim _refillLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TokenBucket"/> class.
    /// </summary>
    /// <param name="maxTokens">Maximum bucket capacity in bytes (tokens).</param>
    /// <param name="tokensPerSecond">Refill rate in bytes per second.</param>
    public TokenBucket(long maxTokens, long tokensPerSecond)
    {
        _maxTokens = maxTokens > 0 ? maxTokens : 1;
        _tokensPerSecond = tokensPerSecond > 0 ? tokensPerSecond : 1;
        _availableTokens = maxTokens;
        _lastRefillTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets the current number of available tokens (for diagnostics).
    /// </summary>
    public long AvailableTokens => Interlocked.Read(ref _availableTokens);

    /// <summary>
    /// Attempts to consume the specified number of tokens. If insufficient tokens are available,
    /// computes the wait time based on refill rate and blocks until tokens are available.
    /// </summary>
    /// <param name="tokens">The number of tokens (bytes) to consume.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when tokens were successfully consumed.</returns>
    public async Task<bool> TryConsumeAsync(long tokens, CancellationToken ct)
    {
        if (tokens <= 0) return true;

        while (!ct.IsCancellationRequested)
        {
            await _refillLock.WaitAsync(ct);
            try
            {
                RefillTokens();

                var available = Interlocked.Read(ref _availableTokens);
                if (available >= tokens)
                {
                    Interlocked.Add(ref _availableTokens, -tokens);
                    return true;
                }

                // Compute wait time for needed tokens
                var deficit = tokens - available;
                var waitSeconds = (double)deficit / _tokensPerSecond;
                var waitMs = (int)Math.Min(Math.Ceiling(waitSeconds * 1000), 1000);

                if (waitMs <= 0) waitMs = 1;

                // Release lock before waiting
                _refillLock.Release();

                await Task.Delay(waitMs, ct);

                // Re-acquire lock and retry (loop continues)
                continue;
            }
            finally
            {
                // Only release if still held (not released in the wait path above)
                if (_refillLock.CurrentCount == 0)
                {
                    try { _refillLock.Release(); }
                    catch (SemaphoreFullException) { /* Already released */ }
                }
            }
        }

        ct.ThrowIfCancellationRequested();
        return false;
    }

    /// <summary>
    /// Blocks until the specified number of tokens are consumed.
    /// Calls <see cref="TryConsumeAsync"/> in a loop until success.
    /// </summary>
    /// <param name="tokens">The number of tokens (bytes) to consume.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ConsumeAsync(long tokens, CancellationToken ct)
    {
        while (!await TryConsumeAsync(tokens, ct))
        {
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Refills tokens based on elapsed time since last refill.
    /// Must be called under the <see cref="_refillLock"/>.
    /// </summary>
    private void RefillTokens()
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastRefillTime;
        if (elapsed.TotalSeconds <= 0) return;

        var newTokens = (long)(elapsed.TotalSeconds * _tokensPerSecond);
        if (newTokens > 0)
        {
            var current = Interlocked.Read(ref _availableTokens);
            var updated = Math.Min(_maxTokens, current + newTokens);
            Interlocked.Exchange(ref _availableTokens, updated);
            _lastRefillTime = now;
        }
    }

    /// <summary>
    /// Releases the semaphore resource.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refillLock.Dispose();
    }
}

/// <summary>
/// A stream wrapper that enforces bandwidth throttling via a <see cref="TokenBucket"/>.
/// All read operations consume tokens after reading, and all write operations consume tokens before writing.
/// Delegates all other stream properties and methods to the inner stream.
/// </summary>
internal sealed class ThrottledStream : Stream
{
    private readonly Stream _inner;
    private readonly TokenBucket _bucket;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThrottledStream"/> class.
    /// </summary>
    /// <param name="inner">The inner stream to wrap.</param>
    /// <param name="bucket">The token bucket for bandwidth enforcement.</param>
    public ThrottledStream(Stream inner, TokenBucket bucket)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _bucket = bucket ?? throw new ArgumentNullException(nameof(bucket));
    }

    /// <inheritdoc/>
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc/>
    public override bool CanSeek => _inner.CanSeek;

    /// <inheritdoc/>
    public override bool CanWrite => _inner.CanWrite;

    /// <inheritdoc/>
    public override long Length => _inner.Length;

    /// <inheritdoc/>
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    /// <inheritdoc/>
    public override bool CanTimeout => _inner.CanTimeout;

    /// <inheritdoc/>
    public override int ReadTimeout
    {
        get => _inner.ReadTimeout;
        set => _inner.ReadTimeout = value;
    }

    /// <inheritdoc/>
    public override int WriteTimeout
    {
        get => _inner.WriteTimeout;
        set => _inner.WriteTimeout = value;
    }

    /// <summary>
    /// Reads from the inner stream and then consumes tokens based on bytes read.
    /// Throttling is applied after reading to avoid holding stream locks unnecessarily.
    /// </summary>
    /// <param name="buffer">The memory buffer to read into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of bytes read.</returns>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var bytesRead = await _inner.ReadAsync(buffer, cancellationToken);
        if (bytesRead > 0)
        {
            await _bucket.ConsumeAsync(bytesRead, cancellationToken);
        }
        return bytesRead;
    }

    /// <summary>
    /// Consumes tokens before writing to the inner stream.
    /// Throttling is applied before writing to enforce upstream backpressure.
    /// </summary>
    /// <param name="buffer">The memory buffer to write from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length > 0)
        {
            await _bucket.ConsumeAsync(buffer.Length, cancellationToken);
        }
        await _inner.WriteAsync(buffer, cancellationToken);
    }

    /// <inheritdoc/>
    public override void Flush() => _inner.Flush();

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = _inner.Read(buffer, offset, count);
        if (bytesRead > 0)
        {
            _bucket.ConsumeAsync(bytesRead, CancellationToken.None).GetAwaiter().GetResult();
        }
        return bytesRead;
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count > 0)
        {
            _bucket.ConsumeAsync(count, CancellationToken.None).GetAwaiter().GetResult();
        }
        _inner.Write(buffer, offset, count);
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    /// <inheritdoc/>
    public override void SetLength(long value) => _inner.SetLength(value);

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        await base.DisposeAsync();
    }
}
