// Licensed to the DataWarehouse project. All rights reserved.
// Soak test configuration with CI and manual presets.

namespace DataWarehouse.Hardening.Tests.Soak;

/// <summary>
/// Configuration class for soak test parameters.
/// Supports CI (10 min), manual 24-hour, and manual 72-hour presets.
/// Duration can be overridden via the SOAK_DURATION_MINUTES environment variable.
/// </summary>
public sealed class SoakTestConfiguration
{
    /// <summary>
    /// Total soak test duration. CI default is 10 minutes.
    /// Overridden by SOAK_DURATION_MINUTES environment variable when set.
    /// </summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Number of concurrent worker tasks generating load.
    /// </summary>
    public int ConcurrentWorkers { get; init; } = 4;

    /// <summary>
    /// Size of each data chunk written/read per operation (bytes).
    /// Default is 1 MB.
    /// </summary>
    public int ChunkSizeBytes { get; init; } = 1_048_576;

    /// <summary>
    /// Interval between GC metric samples.
    /// </summary>
    public TimeSpan GcSampleInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum acceptable Gen2 collections per minute.
    /// Exceeding this threshold fails the soak test.
    /// </summary>
    public int Gen2ThresholdPerMinute { get; init; } = 2;

    /// <summary>
    /// Maximum acceptable working set growth as a percentage of the initial value.
    /// E.g., 10.0 means the working set may grow at most 10% over the entire soak duration.
    /// </summary>
    public double MaxWorkingSetGrowthPercent { get; init; } = 10.0;

    /// <summary>
    /// Minimum delay between worker operations (milliseconds).
    /// </summary>
    public int MinDelayMs { get; init; } = 10;

    /// <summary>
    /// Maximum delay between worker operations (milliseconds).
    /// </summary>
    public int MaxDelayMs { get; init; } = 50;

    /// <summary>
    /// CI preset: 10-minute soak with default thresholds.
    /// </summary>
    public static SoakTestConfiguration CI => new() { Duration = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// Manual 24-hour soak preset with relaxed Gen2 threshold.
    /// </summary>
    public static SoakTestConfiguration Manual24Hr => new()
    {
        Duration = TimeSpan.FromHours(24),
        Gen2ThresholdPerMinute = 3,
        MaxWorkingSetGrowthPercent = 15.0
    };

    /// <summary>
    /// Manual 72-hour soak preset with relaxed thresholds for extended runs.
    /// </summary>
    public static SoakTestConfiguration Manual72Hr => new()
    {
        Duration = TimeSpan.FromHours(72),
        Gen2ThresholdPerMinute = 3,
        MaxWorkingSetGrowthPercent = 20.0
    };

    /// <summary>
    /// Resolves the effective duration, checking the SOAK_DURATION_MINUTES environment variable first.
    /// </summary>
    public TimeSpan EffectiveDuration
    {
        get
        {
            var envValue = Environment.GetEnvironmentVariable("SOAK_DURATION_MINUTES");
            if (!string.IsNullOrEmpty(envValue) && int.TryParse(envValue, out var minutes) && minutes > 0)
            {
                return TimeSpan.FromMinutes(minutes);
            }
            return Duration;
        }
    }
}
