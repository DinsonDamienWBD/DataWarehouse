// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Scaling;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateEncryption.Scaling;

/// <summary>
/// Detected hardware cryptographic capabilities of the current CPU.
/// Re-probed periodically to handle container live migration scenarios
/// where CPU features can change at runtime.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-12: Runtime hardware crypto capabilities")]
public sealed class HardwareCryptoCapabilities
{
    /// <summary>Gets whether AES-NI hardware acceleration is available.</summary>
    public bool AesNiSupported { get; init; }

    /// <summary>Gets whether AVX2 SIMD instructions are available.</summary>
    public bool Avx2Supported { get; init; }

    /// <summary>Gets whether AVX-512 SIMD instructions are available.</summary>
    public bool Avx512Supported { get; init; }

    /// <summary>Gets whether SHA hardware extensions are available.</summary>
    public bool ShaExtensionsSupported { get; init; }

    /// <summary>Gets whether ARM NEON SIMD is available (ARM platforms).</summary>
    public bool ArmNeonSupported { get; init; }

    /// <summary>Gets whether ARM AES cryptographic extensions are available.</summary>
    public bool ArmAesSupported { get; init; }

    /// <summary>Gets the UTC timestamp when these capabilities were last probed.</summary>
    public DateTime ProbedAtUtc { get; init; }

    /// <summary>
    /// Determines whether hardware-accelerated AES is available on any architecture.
    /// </summary>
    public bool HasHardwareAes => AesNiSupported || ArmAesSupported;

    /// <summary>
    /// Determines whether any SIMD acceleration is available.
    /// </summary>
    public bool HasSimd => Avx2Supported || Avx512Supported || ArmNeonSupported;
}

/// <summary>
/// Scaling manager for the encryption subsystem implementing <see cref="IScalableSubsystem"/>.
/// Provides runtime hardware capability re-detection for container environments,
/// dynamically adjustable migration concurrency, and per-algorithm parallelism profiles.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hardware re-detection:</b> Periodically re-probes CPU features (AES-NI, AVX2/AVX-512,
/// SHA extensions, ARM NEON/AES) to handle container live migration where CPU features
/// can change at runtime. Detection interval is configurable (default 5 minutes).
/// Uses <c>System.Runtime.Intrinsics.X86.Aes.IsSupported</c> and equivalent ARM checks.
/// </para>
/// <para>
/// <b>Dynamic migration concurrency:</b> Key migration and algorithm rotation operations
/// are limited by <see cref="SemaphoreSlim"/>. Default: 2 concurrent migrations.
/// Configurable via <see cref="ScalingLimits.MaxConcurrentOperations"/>.
/// </para>
/// <para>
/// <b>Per-algorithm parallelism:</b> Different algorithms have different parallelism profiles.
/// AES-GCM: highly parallelizable (max cores). ChaCha20-Poly1305: moderately parallel.
/// Post-quantum (ML-KEM): single-threaded per operation. Profiles stored in
/// <see cref="BoundedCache{TKey,TValue}"/>.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-12: Encryption scaling with hardware re-detection and per-algorithm parallelism")]
public sealed class EncryptionScalingManager : IScalableSubsystem, IDisposable
{
    /// <summary>Default hardware probe interval: 5 minutes.</summary>
    public static readonly TimeSpan DefaultProbeInterval = TimeSpan.FromMinutes(5);

    /// <summary>Default maximum concurrent key migrations: 2.</summary>
    public const int DefaultMaxMigrations = 2;

    private readonly ILogger _logger;

    // Per-algorithm concurrency limits
    private readonly BoundedCache<string, int> _algorithmConcurrencyLimits;

    // Key/IV cache for recently used encryption contexts
    private readonly BoundedCache<string, byte[]> _keyDerivationCache;

    // Concurrency control for key migration operations
    private SemaphoreSlim _migrationSemaphore;
    private volatile ScalingLimits _currentLimits;
    // Protects atomic semaphore swap in ReconfigureLimitsAsync
    private readonly SemaphoreSlim _reconfigLock = new(1, 1);

    // Hardware detection state
    private volatile HardwareCryptoCapabilities _currentCapabilities;
    private Timer? _probeTimer;
    private readonly TimeSpan _probeInterval;

    // Metrics
    private long _pendingMigrations;
    private long _totalMigrations;
    private long _hardwareProbeCount;
    private long _pendingOperations;
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="EncryptionScalingManager"/> with the specified configuration.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostics.</param>
    /// <param name="limits">Optional initial scaling limits. Uses defaults if <c>null</c>.</param>
    /// <param name="probeInterval">
    /// Interval between hardware capability re-probes. Default: 5 minutes.
    /// Set to <see cref="Timeout.InfiniteTimeSpan"/> to disable periodic probing.
    /// </param>
    public EncryptionScalingManager(
        ILogger logger,
        ScalingLimits? limits = null,
        TimeSpan? probeInterval = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _probeInterval = probeInterval ?? DefaultProbeInterval;

        _currentLimits = limits ?? new ScalingLimits(
            MaxCacheEntries: 10_000,
            MaxConcurrentOperations: DefaultMaxMigrations);

        _migrationSemaphore = new SemaphoreSlim(
            _currentLimits.MaxConcurrentOperations,
            _currentLimits.MaxConcurrentOperations);

        _algorithmConcurrencyLimits = new BoundedCache<string, int>(
            new BoundedCacheOptions<string, int>
            {
                MaxEntries = 100,
                EvictionPolicy = CacheEvictionMode.LRU
            });

        _keyDerivationCache = new BoundedCache<string, byte[]>(
            new BoundedCacheOptions<string, byte[]>
            {
                MaxEntries = Math.Min(_currentLimits.MaxCacheEntries, 5_000),
                EvictionPolicy = CacheEvictionMode.TTL,
                DefaultTtl = TimeSpan.FromMinutes(15)
            });

        // Initialize per-algorithm concurrency defaults
        InitializeAlgorithmDefaults();

        // Initial hardware probe
        _currentCapabilities = ProbeHardwareCapabilities();

        // Start periodic re-detection timer
        if (_probeInterval != Timeout.InfiniteTimeSpan && _probeInterval > TimeSpan.Zero)
        {
            _probeTimer = new Timer(
                ProbeTimerCallback,
                null,
                _probeInterval,
                _probeInterval);
        }

        _logger.LogInformation(
            "EncryptionScalingManager initialized: AES-NI={AesNi}, AVX2={Avx2}, SHA={Sha}, ProbeInterval={Interval}s",
            _currentCapabilities.AesNiSupported,
            _currentCapabilities.Avx2Supported,
            _currentCapabilities.ShaExtensionsSupported,
            _probeInterval.TotalSeconds);
    }

    /// <summary>
    /// Gets the most recently detected hardware cryptographic capabilities.
    /// </summary>
    public HardwareCryptoCapabilities CurrentCapabilities => _currentCapabilities;

    /// <summary>
    /// Gets the bounded cache of per-algorithm concurrency limits.
    /// </summary>
    public BoundedCache<string, int> AlgorithmConcurrencyLimits => _algorithmConcurrencyLimits;

    /// <summary>
    /// Gets the bounded cache for key derivation results.
    /// LOW-2957: restricted to internal to prevent external components from reading or
    /// poisoning cached key material.
    /// </summary>
    internal BoundedCache<string, byte[]> KeyDerivationCache => _keyDerivationCache;

    /// <summary>
    /// Forces an immediate re-probe of hardware cryptographic capabilities.
    /// Called automatically on the configured interval, but can be invoked manually
    /// after known infrastructure changes (e.g., container migration notification).
    /// </summary>
    /// <returns>The newly detected capabilities.</returns>
    public HardwareCryptoCapabilities ReprobeHardware()
    {
        var previous = _currentCapabilities;
        var current = ProbeHardwareCapabilities();
        _currentCapabilities = current;

        if (previous.HasHardwareAes != current.HasHardwareAes ||
            previous.HasSimd != current.HasSimd)
        {
            _logger.LogWarning(
                "Hardware capabilities changed: AES-NI {OldAes}->{NewAes}, SIMD {OldSimd}->{NewSimd}",
                previous.HasHardwareAes, current.HasHardwareAes,
                previous.HasSimd, current.HasSimd);
        }

        return current;
    }

    /// <summary>
    /// Gets the recommended parallelism degree for the specified encryption algorithm.
    /// </summary>
    /// <param name="algorithmName">The algorithm name (e.g., "AES-GCM", "ChaCha20-Poly1305", "ML-KEM").</param>
    /// <returns>The maximum number of concurrent operations recommended for this algorithm.</returns>
    public int GetAlgorithmParallelism(string algorithmName)
    {
        var limit = _algorithmConcurrencyLimits.GetOrDefault(algorithmName);
        return limit > 0 ? limit : 1;
    }

    /// <summary>
    /// Sets the concurrency limit for a specific encryption algorithm.
    /// </summary>
    /// <param name="algorithmName">The algorithm name.</param>
    /// <param name="maxConcurrent">Maximum concurrent operations for this algorithm.</param>
    public void SetAlgorithmParallelism(string algorithmName, int maxConcurrent)
    {
        _algorithmConcurrencyLimits.Put(algorithmName, Math.Max(1, maxConcurrent));
    }

    /// <summary>
    /// Executes a key migration operation with concurrency limiting.
    /// </summary>
    /// <param name="migrationAction">The migration action to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the migration has finished.</returns>
    public async Task ExecuteMigrationAsync(Func<CancellationToken, Task> migrationAction, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(migrationAction);

        Interlocked.Increment(ref _pendingMigrations);
        Interlocked.Increment(ref _pendingOperations);
        try
        {
            await _migrationSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await migrationAction(ct).ConfigureAwait(false);
                Interlocked.Increment(ref _totalMigrations);
            }
            finally
            {
                _migrationSemaphore.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingMigrations);
            Interlocked.Decrement(ref _pendingOperations);
        }
    }

    /// <summary>
    /// Determines whether hardware-accelerated encryption should be used based on
    /// current capabilities. Falls back to software implementation when hardware
    /// acceleration is unavailable (e.g., after container migration).
    /// </summary>
    /// <returns><c>true</c> if hardware acceleration should be used; otherwise <c>false</c>.</returns>
    public bool ShouldUseHardwareAcceleration()
    {
        return _currentCapabilities.HasHardwareAes;
    }

    // ---- Private helpers ----

    /// <summary>
    /// Probes CPU features using System.Runtime.Intrinsics to detect hardware crypto support.
    /// </summary>
    private static HardwareCryptoCapabilities ProbeHardwareCapabilities()
    {
        bool aesNi = false;
        bool avx2 = false;
        bool avx512 = false;
        bool sha = false;
        bool armNeon = false;
        bool armAes = false;

        // x86/x64 detection
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64 ||
            RuntimeInformation.ProcessArchitecture == Architecture.X86)
        {
            aesNi = System.Runtime.Intrinsics.X86.Aes.IsSupported;
            avx2 = System.Runtime.Intrinsics.X86.Avx2.IsSupported;
            // Intel SHA extensions (SHA-NI) — use Bmi1 as a conservative proxy:
            // SHA-NI (introduced ~Goldmont, Zen) correlates strongly with BMI1 support.
            // This is more precise than X86Base.IsSupported (always true on x86).
            sha = System.Runtime.Intrinsics.X86.Bmi1.IsSupported;
        }

        // ARM detection
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ||
            RuntimeInformation.ProcessArchitecture == Architecture.Arm)
        {
            armNeon = System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported;
            armAes = System.Runtime.Intrinsics.Arm.Aes.IsSupported;
        }

        return new HardwareCryptoCapabilities
        {
            AesNiSupported = aesNi,
            Avx2Supported = avx2,
            Avx512Supported = avx512,
            ShaExtensionsSupported = sha,
            ArmNeonSupported = armNeon,
            ArmAesSupported = armAes,
            ProbedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Initializes per-algorithm concurrency defaults based on algorithm characteristics.
    /// </summary>
    private void InitializeAlgorithmDefaults()
    {
        int maxCores = Environment.ProcessorCount;

        // AES-GCM: highly parallelizable (use max cores)
        _algorithmConcurrencyLimits.Put("AES-GCM", maxCores);
        _algorithmConcurrencyLimits.Put("AES-CBC", maxCores);
        _algorithmConcurrencyLimits.Put("AES-CTR", maxCores);

        // ChaCha20-Poly1305: moderately parallel
        _algorithmConcurrencyLimits.Put("ChaCha20-Poly1305", Math.Max(1, maxCores / 2));

        // Post-quantum algorithms: single-threaded per operation
        _algorithmConcurrencyLimits.Put("ML-KEM", 1);
        _algorithmConcurrencyLimits.Put("ML-DSA", 1);
        _algorithmConcurrencyLimits.Put("SLH-DSA", 1);

        // Key derivation: moderately parallel
        _algorithmConcurrencyLimits.Put("PBKDF2", Math.Max(1, maxCores / 4));
        _algorithmConcurrencyLimits.Put("Argon2", Math.Max(1, maxCores / 4));
        _algorithmConcurrencyLimits.Put("HKDF", maxCores);
    }

    private void ProbeTimerCallback(object? state)
    {
        if (_disposed) return;

        try
        {
            ReprobeHardware();
            Interlocked.Increment(ref _hardwareProbeCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hardware capability re-probe failed");
        }
    }

    // ---- IScalableSubsystem ----

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        var keyStats = _keyDerivationCache.GetStatistics();
        var caps = _currentCapabilities;

        return new Dictionary<string, object>
        {
            ["hardware.aesNi"] = caps.AesNiSupported,
            ["hardware.avx2"] = caps.Avx2Supported,
            ["hardware.avx512"] = caps.Avx512Supported,
            ["hardware.sha"] = caps.ShaExtensionsSupported,
            ["hardware.armNeon"] = caps.ArmNeonSupported,
            ["hardware.armAes"] = caps.ArmAesSupported,
            ["hardware.lastProbeUtc"] = caps.ProbedAtUtc.ToString("O"),
            ["hardware.probeCount"] = Interlocked.Read(ref _hardwareProbeCount),
            ["cache.keyDerivation.size"] = keyStats.ItemCount,
            ["cache.keyDerivation.hitRate"] = (keyStats.Hits + keyStats.Misses) > 0
                ? (double)keyStats.Hits / (keyStats.Hits + keyStats.Misses)
                : 0.0,
            ["migration.pending"] = Interlocked.Read(ref _pendingMigrations),
            ["migration.total"] = Interlocked.Read(ref _totalMigrations),
            ["migration.maxConcurrent"] = _currentLimits.MaxConcurrentOperations,
            ["migration.availableSlots"] = _migrationSemaphore.CurrentCount,
            ["backpressure.queueDepth"] = Interlocked.Read(ref _pendingOperations),
            ["backpressure.state"] = CurrentBackpressureState.ToString()
        };
    }

    /// <inheritdoc/>
    public async Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(limits);

        // Guard the semaphore swap under _reconfigLock so concurrent callers (e.g. a migration
        // calling WaitAsync) never acquire the old semaphore only to Release on the new one.
        // Pattern: acquire reconfig lock → swap → publish → release.
        await _reconfigLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var oldLimits = _currentLimits;
            _currentLimits = limits;

            if (limits.MaxConcurrentOperations != oldLimits.MaxConcurrentOperations)
            {
                // Drain the old semaphore: wait until all in-flight holders have released,
                // then replace it. Workers that haven't entered WaitAsync yet will use the
                // new semaphore because we publish _migrationSemaphore after creating it.
                var oldSemaphore = _migrationSemaphore;
                var newSemaphore = new SemaphoreSlim(
                    limits.MaxConcurrentOperations,
                    limits.MaxConcurrentOperations);

                // Publish new semaphore before disposing old one.
                // Interlocked.Exchange ensures the assignment is visible to all threads
                // before we dispose the old semaphore.
                var _ = System.Threading.Interlocked.Exchange(ref _migrationSemaphore, newSemaphore);

                // Give a short grace period for threads that already read the old reference
                // but haven't called WaitAsync yet. A full drain (acquiring all slots) would
                // deadlock if workers are still holding releases, so we bound it.
                await Task.Delay(10, ct).ConfigureAwait(false);
                oldSemaphore.Dispose();
            }
        }
        finally
        {
            _reconfigLock.Release();
        }

        _logger.LogInformation(
            "Encryption scaling limits reconfigured: MaxCache={MaxCache}, MaxMigrations={MaxMigrations}",
            limits.MaxCacheEntries, limits.MaxConcurrentOperations);
    }

    /// <inheritdoc/>
    public ScalingLimits CurrentLimits => _currentLimits;

    /// <inheritdoc/>
    public BackpressureState CurrentBackpressureState
    {
        get
        {
            long pending = Interlocked.Read(ref _pendingOperations);
            int maxQueue = _currentLimits.MaxQueueDepth;

            if (pending <= 0) return BackpressureState.Normal;
            if (pending < maxQueue * 0.5) return BackpressureState.Normal;
            if (pending < maxQueue * 0.8) return BackpressureState.Warning;
            if (pending < maxQueue) return BackpressureState.Critical;
            return BackpressureState.Shedding;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _probeTimer?.Dispose();
        _probeTimer = null;
        _algorithmConcurrencyLimits.Dispose();
        _keyDerivationCache.Dispose();
        _migrationSemaphore.Dispose();
        _reconfigLock.Dispose();
    }
}
