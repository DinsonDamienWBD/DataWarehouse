using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Infrastructure.Intelligence;

/// <summary>
/// Storage speed classification for the underlying storage device.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-02)")]
public enum StorageSpeedClass
{
    /// <summary>Storage speed has not been determined.</summary>
    Unknown = 0,

    /// <summary>Traditional spinning hard disk drive.</summary>
    Hdd = 1,

    /// <summary>SATA-connected solid-state drive.</summary>
    Sata = 2,

    /// <summary>NVMe-connected solid-state drive.</summary>
    NvMe = 3
}

/// <summary>
/// Thermal state of the hardware as inferred from observation latency patterns.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-02)")]
public enum ThermalState
{
    /// <summary>No thermal concerns detected.</summary>
    Normal = 0,

    /// <summary>Elevated latency patterns suggest possible thermal pressure.</summary>
    Warm = 1,

    /// <summary>Significant latency degradation consistent with CPU throttling.</summary>
    Throttling = 2,

    /// <summary>Severe thermal throttling detected — sustained high latency.</summary>
    Critical = 3
}

/// <summary>
/// Point-in-time snapshot of hardware capabilities and resource state.
/// </summary>
/// <param name="LogicalProcessors">Number of logical CPU cores available.</param>
/// <param name="HasAvx2">Whether AVX2 SIMD instructions are supported.</param>
/// <param name="HasAes">Whether hardware AES instructions are supported.</param>
/// <param name="HasSse42">Whether SSE4.2 instructions are supported.</param>
/// <param name="HasArmAdvSimd">Whether ARM Advanced SIMD (NEON) is supported.</param>
/// <param name="HasArmAes">Whether ARM hardware AES is supported.</param>
/// <param name="TotalMemoryBytes">Total physical memory available to the process.</param>
/// <param name="AvailableMemoryBytes">Currently available memory bytes.</param>
/// <param name="MemoryPressurePercent">Percentage of memory currently in use (0-100).</param>
/// <param name="StorageSpeed">Classified storage speed of the primary storage device.</param>
/// <param name="Thermal">Current thermal state as inferred from latency patterns.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-02)")]
public sealed record HardwareSnapshot(
    int LogicalProcessors,
    bool HasAvx2,
    bool HasAes,
    bool HasSse42,
    bool HasArmAdvSimd,
    bool HasArmAes,
    long TotalMemoryBytes,
    long AvailableMemoryBytes,
    double MemoryPressurePercent,
    StorageSpeedClass StorageSpeed,
    ThermalState Thermal
);

/// <summary>
/// AI advisor that probes hardware capabilities and tracks resource state.
/// Detects CPU SIMD capabilities (x86 AVX2/AES/SSE4.2, ARM AdvSIMD/AES),
/// RAM pressure, storage speed class, and thermal throttling state inferred
/// from observation latency patterns.
/// </summary>
/// <remarks>
/// AIPI-02: Hardware-aware context for AI policy recommendations.
/// The probe refreshes its snapshot on a 30-second cooldown to avoid
/// excessive GC/process info queries. Thermal state is inferred by comparing
/// p99 to p50 observation latency — a ratio above 3x indicates thermal pressure.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-02)")]
public sealed class HardwareProbe : IAiAdvisor
{
    private volatile HardwareSnapshot _currentSnapshot;
    private long _lastRefreshTicks;
    private readonly long _refreshCooldownTicks;

    // Latency tracking for thermal inference
    private readonly double[] _latencySamples;
    private int _latencySampleIndex;
    private int _latencySampleCount;
    private const int LatencySampleCapacity = 256;

    /// <summary>
    /// Default refresh cooldown period (30 seconds).
    /// </summary>
    private static readonly long DefaultCooldownTicks =
        (long)(30.0 * Stopwatch.Frequency);

    /// <summary>
    /// Creates a new HardwareProbe advisor.
    /// </summary>
    /// <param name="refreshCooldownSeconds">
    /// Minimum seconds between hardware snapshot refreshes. Default 30.
    /// </param>
    public HardwareProbe(double refreshCooldownSeconds = 30.0)
    {
        _refreshCooldownTicks = refreshCooldownSeconds > 0
            ? (long)(refreshCooldownSeconds * Stopwatch.Frequency)
            : DefaultCooldownTicks;

        _latencySamples = new double[LatencySampleCapacity];
        _latencySampleIndex = 0;
        _latencySampleCount = 0;
        _lastRefreshTicks = 0;

        // Take initial snapshot
        _currentSnapshot = BuildSnapshot(ThermalState.Normal);
    }

    /// <inheritdoc />
    public string AdvisorId => "hardware_probe";

    /// <summary>
    /// Latest hardware snapshot. Updated via volatile reference swap.
    /// </summary>
    public HardwareSnapshot CurrentSnapshot => _currentSnapshot;

    /// <summary>
    /// True if the hardware is under resource pressure: memory usage above 85%
    /// or thermal state is Warm or worse.
    /// </summary>
    public bool IsHardwareConstrained =>
        _currentSnapshot.MemoryPressurePercent > 85.0 ||
        _currentSnapshot.Thermal >= ThermalState.Warm;

    /// <inheritdoc />
    public Task ProcessObservationsAsync(
        IReadOnlyList<ObservationEvent> batch,
        CancellationToken ct)
    {
        if (batch.Count == 0) return Task.CompletedTask;

        // Track observation latencies for thermal inference
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = 0; i < batch.Count; i++)
        {
            double latencyMs = (now - batch[i].Timestamp).TotalMilliseconds;
            if (latencyMs >= 0)
            {
                RecordLatency(latencyMs);
            }
        }

        // Check if enough time has elapsed for a snapshot refresh
        long currentTicks = Stopwatch.GetTimestamp();
        long lastRefresh = Volatile.Read(ref _lastRefreshTicks);

        if (currentTicks - lastRefresh >= _refreshCooldownTicks)
        {
            Volatile.Write(ref _lastRefreshTicks, currentTicks);
            ThermalState thermal = InferThermalState();
            _currentSnapshot = BuildSnapshot(thermal);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Forces an immediate hardware snapshot refresh.
    /// </summary>
    public void RefreshSnapshot()
    {
        Volatile.Write(ref _lastRefreshTicks, Stopwatch.GetTimestamp());
        ThermalState thermal = InferThermalState();
        _currentSnapshot = BuildSnapshot(thermal);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordLatency(double latencyMs)
    {
        int idx = _latencySampleIndex;
        _latencySamples[idx] = latencyMs;
        _latencySampleIndex = (idx + 1) % LatencySampleCapacity;
        if (_latencySampleCount < LatencySampleCapacity)
        {
            _latencySampleCount++;
        }
    }

    private ThermalState InferThermalState()
    {
        int count = _latencySampleCount;
        if (count < 10) return ThermalState.Normal;

        // Copy current samples for sorting
        double[] sorted = new double[count];
        Array.Copy(_latencySamples, sorted, count);
        Array.Sort(sorted);

        double p50 = sorted[count / 2];
        double p99 = sorted[(int)(count * 0.99)];

        if (p50 <= 0) return ThermalState.Normal;

        double ratio = p99 / p50;

        if (ratio > 10.0) return ThermalState.Critical;
        if (ratio > 5.0) return ThermalState.Throttling;
        if (ratio > 3.0) return ThermalState.Warm;

        return ThermalState.Normal;
    }

    private static HardwareSnapshot BuildSnapshot(ThermalState thermal)
    {
        int logicalProcessors = Environment.ProcessorCount;

        // CPU SIMD capability detection — x86
        bool hasAvx2 = false;
        bool hasAes = false;
        bool hasSse42 = false;

#if NET6_0_OR_GREATER
        hasAvx2 = System.Runtime.Intrinsics.X86.Avx2.IsSupported;
        hasAes = System.Runtime.Intrinsics.X86.Aes.IsSupported;
        hasSse42 = System.Runtime.Intrinsics.X86.Sse42.IsSupported;
#endif

        // CPU SIMD capability detection — ARM (cross-platform)
        bool hasArmAdvSimd = false;
        bool hasArmAes = false;

#if NET6_0_OR_GREATER
        hasArmAdvSimd = System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported;
        hasArmAes = System.Runtime.Intrinsics.Arm.Aes.IsSupported;
#endif

        // Memory detection via GC
        long totalMemory;
        long availableMemory;
        double memoryPressure;

        try
        {
            GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
            totalMemory = gcInfo.TotalAvailableMemoryBytes;

            // Available = total minus committed (heap + fragmentation)
            long committed = gcInfo.HeapSizeBytes + gcInfo.FragmentedBytes;
            availableMemory = Math.Max(0, totalMemory - committed);
            memoryPressure = totalMemory > 0
                ? (double)(totalMemory - availableMemory) / totalMemory * 100.0
                : 0.0;
        }
        catch (Exception)
        {
            // Fallback for restricted environments
            totalMemory = 0;
            availableMemory = 0;
            memoryPressure = 0;
        }

        // Storage speed detection — environment variable hint
        StorageSpeedClass storageSpeed = DetectStorageSpeed();

        return new HardwareSnapshot(
            LogicalProcessors: logicalProcessors,
            HasAvx2: hasAvx2,
            HasAes: hasAes,
            HasSse42: hasSse42,
            HasArmAdvSimd: hasArmAdvSimd,
            HasArmAes: hasArmAes,
            TotalMemoryBytes: totalMemory,
            AvailableMemoryBytes: availableMemory,
            MemoryPressurePercent: Math.Round(memoryPressure, 2),
            StorageSpeed: storageSpeed,
            Thermal: thermal
        );
    }

    private static StorageSpeedClass DetectStorageSpeed()
    {
        string? envHint = Environment.GetEnvironmentVariable("DATAWAREHOUSE_STORAGE_SPEED");

        if (string.IsNullOrWhiteSpace(envHint))
        {
            return StorageSpeedClass.Unknown;
        }

        return envHint.Trim().ToUpperInvariant() switch
        {
            "HDD" => StorageSpeedClass.Hdd,
            "SATA" or "SSD" => StorageSpeedClass.Sata,
            "NVME" or "PCIE" => StorageSpeedClass.NvMe,
            _ => StorageSpeedClass.Unknown
        };
    }
}
