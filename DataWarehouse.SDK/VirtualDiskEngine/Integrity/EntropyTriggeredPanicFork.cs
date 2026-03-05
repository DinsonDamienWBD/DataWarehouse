using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Integrity;

// ─── Value types ─────────────────────────────────────────────────────────────

/// <summary>
/// Running statistics maintained by the exponential-moving-average baseline tracker.
/// </summary>
public readonly struct EntropyBaseline
{
    /// <summary>Exponentially-weighted mean Shannon entropy (bits/byte).</summary>
    public double Mean { get; init; }

    /// <summary>Exponentially-weighted standard deviation of Shannon entropy.</summary>
    public double StandardDeviation { get; init; }

    /// <summary>Total number of writes incorporated into the running statistics.</summary>
    public long SampleCount { get; init; }
}

/// <summary>
/// Result of analysing a single write's Shannon entropy against the current baseline.
/// </summary>
public readonly struct AnomalyResult
{
    /// <summary>Shannon entropy of the analysed write data (bits/byte, range 0.0–8.0).</summary>
    public double Entropy { get; init; }

    /// <summary>Current baseline mean at the time of analysis.</summary>
    public double BaselineMean { get; init; }

    /// <summary>Current baseline standard deviation at the time of analysis.</summary>
    public double BaselineStdDev { get; init; }

    /// <summary>Whether the write was classified as anomalous.</summary>
    public bool IsAnomaly { get; init; }

    /// <summary>Human-readable explanation of the anomaly, or <c>null</c> if none.</summary>
    public string? AnomalyReason { get; init; }

    /// <summary>
    /// Number of standard deviations the observed entropy is above the current baseline mean.
    /// Negative values mean the entropy was below the baseline.
    /// </summary>
    public double DeviationsFromBaseline { get; init; }
}

/// <summary>
/// Describes a panic fork that was triggered in response to an entropy anomaly.
/// </summary>
public readonly struct PanicForkResult
{
    /// <summary>Identifier of the immutable snapshot created by the panic fork.</summary>
    public long SnapshotId { get; init; }

    /// <summary>Shannon entropy of the write that triggered the fork (bits/byte).</summary>
    public double TriggeringEntropy { get; init; }

    /// <summary>Baseline mean entropy at the time of the fork.</summary>
    public double BaselineEntropy { get; init; }

    /// <summary>Human-readable explanation of why the fork was triggered.</summary>
    public string Reason { get; init; }

    /// <summary>UTC timestamp when the fork was created.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// <c>true</c> when the triggering write was rejected (aggressive mode / block response).
    /// </summary>
    public bool WriteBlocked { get; init; }
}

/// <summary>
/// Aggregated monitoring statistics returned by
/// <see cref="EntropyTriggeredPanicFork.GetStats"/>.
/// </summary>
public readonly struct EntropyMonitorStats
{
    /// <summary>Total number of write payloads that have been entropy-analysed.</summary>
    public long WritesAnalyzed { get; init; }

    /// <summary>Total number of panic forks triggered since the instance was created.</summary>
    public int PanicForksTriggered { get; init; }

    /// <summary>Current exponentially-weighted mean baseline entropy (bits/byte).</summary>
    public double CurrentBaselineMean { get; init; }

    /// <summary>Current exponentially-weighted baseline standard deviation.</summary>
    public double CurrentBaselineStdDev { get; init; }

    /// <summary>Shannon entropy computed for the most-recent analysed write.</summary>
    public double LastEntropy { get; init; }
}

// ─── Core implementation ──────────────────────────────────────────────────────

/// <summary>
/// Monitors Shannon entropy of incoming write data and automatically creates an immutable
/// snapshot (panic fork) when anomalous entropy patterns are detected.
/// </summary>
/// <remarks>
/// <para>
/// Ransomware, encryption attacks, and bulk-corruption events cause dramatic increases in the
/// byte-level randomness of write payloads.  By computing the Shannon entropy of each write and
/// comparing it against a continuously-updated baseline, this class can detect these attacks
/// before the suspect data is permanently committed and fork a clean, read-only recovery snapshot.
/// </para>
/// <para>
/// Baseline statistics are maintained via exponential moving average (EMA) so that the detector
/// adapts to legitimate workload changes (e.g., switching from mostly-text to mostly-compressed
/// data) while remaining sensitive to sudden, abnormal entropy spikes.
/// </para>
/// <para>
/// A sliding circular window averages entropy across <see cref="PanicForkConfig.WindowSizeBlocks"/>
/// consecutive writes to reduce false positives caused by individual high-entropy blocks (e.g.,
/// a single pre-compressed attachment).
/// </para>
/// <para>
/// Rate limiting via <see cref="PanicForkConfig.MaxPanicForksPerHour"/> prevents snapshot storms
/// under a sustained, high-frequency attack.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VOPT-57 entropy-triggered panic fork")]
public sealed class EntropyTriggeredPanicFork
{
    // ── EMA smoothing factor for baseline mean (α ≈ 0.001 gives ~1000-sample memory) ──
    private const double Alpha = 0.001;

    private readonly PanicForkConfig _config;

    // ── Baseline running statistics ───────────────────────────────────────────
    private double _baselineMean;
    private double _baselineVariance; // Welford-style variance for EMA
    private long _sampleCount;

    // ── Sliding circular window ───────────────────────────────────────────────
    private readonly double[] _window;
    private int _windowHead;
    private int _windowFill; // number of valid entries (0..WindowSizeBlocks)
    private double _windowSum;

    // ── Rate limiting ─────────────────────────────────────────────────────────
    private readonly Queue<DateTimeOffset> _forkTimestamps = new();

    // ── Monitoring counters ───────────────────────────────────────────────────
    private long _writesAnalyzed;
    private int _panicForksTriggered;
    private double _lastEntropy;

    // ── Synchronisation ───────────────────────────────────────────────────────
    // A single lock is sufficient: entropy computation is fast (microseconds) and panic-fork
    // creation is delegated to the caller-supplied async delegate, so we never hold the lock
    // across I/O.
    private readonly object _lock = new();

    /// <summary>
    /// Fired each time a panic fork is successfully created.
    /// Subscribers receive the full <see cref="PanicForkResult"/> for logging or alerting.
    /// The event is raised outside the internal lock.
    /// </summary>
    public event Action<PanicForkResult>? OnPanicFork;

    /// <summary>
    /// Initialises a new <see cref="EntropyTriggeredPanicFork"/> instance.
    /// </summary>
    /// <param name="config">Behaviour configuration.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> is null.</exception>
    public EntropyTriggeredPanicFork(PanicForkConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _window = new double[Math.Max(1, config.WindowSizeBlocks)];

        // Prime the baseline mean at the midpoint of the expected static range so that
        // the very first writes are evaluated sensibly before enough samples accumulate.
        _baselineMean = (config.EntropyBaselineMin + config.EntropyBaselineMax) / 2.0;
        _baselineVariance = Math.Pow((config.EntropyBaselineMax - config.EntropyBaselineMin) / 4.0, 2);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current running baseline statistics.
    /// </summary>
    public EntropyBaseline CurrentBaseline
    {
        get
        {
            lock (_lock)
            {
                return new EntropyBaseline
                {
                    Mean = _baselineMean,
                    StandardDeviation = Math.Sqrt(_baselineVariance),
                    SampleCount = _sampleCount
                };
            }
        }
    }

    /// <summary>
    /// Whether enough writes have been observed for the baseline to be trusted.
    /// </summary>
    public bool IsBaselineTrusted
    {
        get
        {
            lock (_lock) { return _sampleCount >= _config.MinWritesBeforeBaseline; }
        }
    }

    /// <summary>
    /// Computes the Shannon entropy (bits/byte) of <paramref name="data"/>.
    /// </summary>
    /// <param name="data">The payload to analyse. May be empty (returns 0.0).</param>
    /// <returns>Entropy in the range [0.0, 8.0], where 8.0 = perfectly random bytes.</returns>
    public static double ComputeShannonEntropy(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return 0.0;

        // Build byte-frequency histogram.
        Span<int> freq = stackalloc int[256];
        foreach (byte b in data)
            freq[b]++;

        int n = data.Length;
        double entropy = 0.0;
        for (int i = 0; i < 256; i++)
        {
            if (freq[i] == 0)
                continue;
            double p = (double)freq[i] / n;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }

    /// <summary>
    /// Analyses write data for entropy anomalies and updates running baseline statistics.
    /// </summary>
    /// <param name="writeData">The raw bytes about to be written to the block device.</param>
    /// <returns>An <see cref="AnomalyResult"/> describing the outcome.</returns>
    public AnomalyResult AnalyzeWrite(ReadOnlySpan<byte> writeData)
    {
        double entropy = ComputeShannonEntropy(writeData);

        lock (_lock)
        {
            // Update the sliding window first (so window average includes this write).
            AddToWindow(entropy);

            // Use the window average as the representative entropy for this write batch.
            double smoothedEntropy = _windowFill > 0 ? _windowSum / _windowFill : entropy;

            // Capture baseline before updating so the result reflects pre-write state.
            double bMean = _baselineMean;
            double bStdDev = Math.Sqrt(_baselineVariance);
            bool trusted = _sampleCount >= _config.MinWritesBeforeBaseline;

            // Update running baseline.
            UpdateBaseline(entropy);

            _writesAnalyzed++;
            _lastEntropy = entropy;

            // ── Anomaly detection ─────────────────────────────────────────────
            bool isAnomaly = false;
            string? reason = null;
            double deviations = bStdDev > 0.0 ? (smoothedEntropy - bMean) / bStdDev : 0.0;

            if (smoothedEntropy > _config.EntropyThreshold)
            {
                isAnomaly = true;
                reason = $"Entropy {smoothedEntropy:F4} exceeds absolute ceiling {_config.EntropyThreshold:F4}";
            }
            else if (trusted && bStdDev > 0.0 &&
                     smoothedEntropy > bMean + _config.AnomalyDeviationFactor * bStdDev)
            {
                isAnomaly = true;
                reason = $"Entropy {smoothedEntropy:F4} is {deviations:F2} stddevs above baseline mean {bMean:F4} " +
                         $"(threshold: {_config.AnomalyDeviationFactor:F1} stddevs)";
            }

            return new AnomalyResult
            {
                Entropy = smoothedEntropy,
                BaselineMean = bMean,
                BaselineStdDev = bStdDev,
                IsAnomaly = isAnomaly,
                AnomalyReason = reason,
                DeviationsFromBaseline = deviations
            };
        }
    }

    /// <summary>
    /// Evaluates write data for entropy anomalies and, if an anomaly is detected and the
    /// rate limit has not been exceeded, calls <paramref name="createSnapshotFunc"/> to fork an
    /// immutable snapshot before the write is committed.
    /// </summary>
    /// <param name="writeData">The raw bytes about to be written to the block device.</param>
    /// <param name="createSnapshotFunc">
    /// Delegate that creates the snapshot.  Receives (snapshotName, isReadOnly=true) and returns
    /// the new snapshot identifier.  The delegate is called <em>outside</em> the internal lock and
    /// may perform async I/O.
    /// </param>
    /// <param name="ct">Cancellation token forwarded to <paramref name="createSnapshotFunc"/>.</param>
    /// <returns>
    /// A <see cref="PanicForkResult"/> when a fork was triggered, or <c>null</c> when the write
    /// was considered normal or the rate limit was active.
    /// </returns>
    public Task<PanicForkResult?> EvaluateAndForkAsync(
        ReadOnlySpan<byte> writeData,
        Func<string, bool, Task<long>> createSnapshotFunc,
        CancellationToken ct = default)
    {
        // ReadOnlySpan<byte> cannot be used in async methods; copy data to a heap array first
        // so the async continuation can reference it safely after the stack unwinds.
        byte[] writeCopy = writeData.ToArray();
        return EvaluateAndForkCoreAsync(writeCopy, createSnapshotFunc, ct);
    }

    /// <inheritdoc cref="EvaluateAndForkAsync(ReadOnlySpan{byte},Func{string,bool,Task{long}},CancellationToken)"/>
    private async Task<PanicForkResult?> EvaluateAndForkCoreAsync(
        byte[] writeData,
        Func<string, bool, Task<long>> createSnapshotFunc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(createSnapshotFunc);

        // AnalyzeWrite is lock-safe and fast; call it synchronously.
        AnomalyResult result = AnalyzeWrite(writeData);

        if (!result.IsAnomaly)
            return null;

        // Check rate limit outside the main data-path lock.
        bool rateLimitExceeded;
        lock (_lock)
        {
            rateLimitExceeded = IsRateLimited();
        }

        if (rateLimitExceeded)
            return null;

        // Determine whether this write should be blocked.
        bool blockWrite = _config.Response == PanicResponse.SnapshotAndBlock ||
                          _config.BlockSuspectWrites;

        // Build a deterministic snapshot name: panic-fork-{UTC-ticks}.
        string snapshotName = $"panic-fork-{DateTimeOffset.UtcNow.Ticks}";

        // Delegate snapshot creation to the caller — this is async I/O; must NOT hold any lock.
        long snapshotId = await createSnapshotFunc(snapshotName, /* isReadOnly */ true)
            .ConfigureAwait(false);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        PanicForkResult forkResult = new()
        {
            SnapshotId = snapshotId,
            TriggeringEntropy = result.Entropy,
            BaselineEntropy = result.BaselineMean,
            Reason = result.AnomalyReason ?? "Entropy anomaly detected",
            Timestamp = now,
            WriteBlocked = blockWrite
        };

        // Record the fork timestamp for rate limiting and update counter.
        lock (_lock)
        {
            _forkTimestamps.Enqueue(now);
            _panicForksTriggered++;
        }

        // Fire event outside lock.
        OnPanicFork?.Invoke(forkResult);

        return forkResult;
    }

    /// <summary>
    /// Returns a point-in-time snapshot of monitoring statistics.
    /// </summary>
    public EntropyMonitorStats GetStats()
    {
        lock (_lock)
        {
            return new EntropyMonitorStats
            {
                WritesAnalyzed = _writesAnalyzed,
                PanicForksTriggered = _panicForksTriggered,
                CurrentBaselineMean = _baselineMean,
                CurrentBaselineStdDev = Math.Sqrt(_baselineVariance),
                LastEntropy = _lastEntropy
            };
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private helpers — all called under _lock
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Updates the exponential moving average baseline with a new entropy observation.
    /// Uses Welford-style EMA variance to avoid catastrophic cancellation.
    /// Called under <see cref="_lock"/>.
    /// </summary>
    private void UpdateBaseline(double entropy)
    {
        _sampleCount++;

        if (_sampleCount == 1)
        {
            // First real sample: anchor the mean to this observation.
            _baselineMean = entropy;
            _baselineVariance = 0.0;
            return;
        }

        double delta = entropy - _baselineMean;
        _baselineMean += Alpha * delta;
        double delta2 = entropy - _baselineMean;
        // EMA variance update: V_new = (1-α)*V_old + α*(Δ*Δ2)
        _baselineVariance = (1.0 - Alpha) * _baselineVariance + Alpha * (delta * delta2);

        // Clamp variance to a small positive floor to avoid division by zero.
        if (_baselineVariance < 1e-9)
            _baselineVariance = 1e-9;
    }

    /// <summary>
    /// Adds an entropy sample to the circular sliding window and keeps the running sum current.
    /// Called under <see cref="_lock"/>.
    /// </summary>
    private void AddToWindow(double entropy)
    {
        if (_windowFill < _window.Length)
        {
            // Window not yet full — just fill in order.
            _window[_windowHead] = entropy;
            _windowSum += entropy;
            _windowFill++;
            _windowHead = (_windowHead + 1) % _window.Length;
        }
        else
        {
            // Window is full — overwrite the oldest entry.
            _windowSum -= _window[_windowHead]; // subtract outgoing value
            _window[_windowHead] = entropy;
            _windowSum += entropy;
            _windowHead = (_windowHead + 1) % _window.Length;
        }
    }

    /// <summary>
    /// Returns <c>true</c> if the rate limit for panic forks is currently active.
    /// Prunes stale timestamps older than one hour before checking.
    /// Called under <see cref="_lock"/>.
    /// </summary>
    private bool IsRateLimited()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddHours(-1);

        // Remove timestamps older than 1 hour.
        while (_forkTimestamps.Count > 0 && _forkTimestamps.Peek() < cutoff)
            _forkTimestamps.Dequeue();

        return _forkTimestamps.Count >= _config.MaxPanicForksPerHour;
    }
}
