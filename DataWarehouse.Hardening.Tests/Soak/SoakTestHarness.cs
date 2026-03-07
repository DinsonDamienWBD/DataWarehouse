// Licensed to the DataWarehouse project. All rights reserved.
// Parameterized soak test harness with configurable duration and GC monitoring.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Xunit.Abstractions;

namespace DataWarehouse.Hardening.Tests.Soak;

/// <summary>
/// Tracks per-worker statistics during a soak test run.
/// </summary>
public sealed class WorkerStats
{
    public long OperationsCompleted;
    public long BytesWritten;
    public long BytesRead;
    public long Errors;
}

/// <summary>
/// Parameterized soak test harness that applies moderate continuous read/write load
/// while monitoring GC event counters. Supports CI (10 min) and manual (24-72 hr) presets.
/// </summary>
[Trait("Category", "Soak")]
public sealed class SoakTestHarness : IDisposable
{
    private readonly ITestOutputHelper _output;

    public SoakTestHarness(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// CI soak test: 10-minute duration with moderate read/write load and GC monitoring.
    /// Asserts Gen2 rate, working set growth, no monotonic growth, and no worker exceptions.
    /// </summary>
    [Fact]
    [Trait("Duration", "CI")]
    public async Task Test_SoakCI_10Minutes()
    {
        var config = SoakTestConfiguration.CI;
        await RunSoakTest(config);
    }

    /// <summary>
    /// Manual 24-hour soak test. Skipped by default; run explicitly for extended validation.
    /// </summary>
    [Fact(Skip = "Manual 24hr soak - run explicitly")]
    [Trait("Duration", "Manual")]
    public async Task Test_Soak_24Hours()
    {
        var config = SoakTestConfiguration.Manual24Hr;
        await RunSoakTest(config);
    }

    /// <summary>
    /// Manual 72-hour soak test. Skipped by default; run explicitly for extended validation.
    /// </summary>
    [Fact(Skip = "Manual 72hr soak - run explicitly")]
    [Trait("Duration", "Manual")]
    public async Task Test_Soak_72Hours()
    {
        var config = SoakTestConfiguration.Manual72Hr;
        await RunSoakTest(config);
    }

    /// <summary>
    /// Core soak test runner. Starts GC monitoring, spawns concurrent workers performing
    /// continuous read/write operations, waits for duration, then asserts GC and memory thresholds.
    /// </summary>
    private async Task RunSoakTest(SoakTestConfiguration config)
    {
        var effectiveDuration = config.EffectiveDuration;
        _output.WriteLine($"=== Soak Test Starting ===");
        _output.WriteLine($"Duration: {effectiveDuration}");
        _output.WriteLine($"Workers: {config.ConcurrentWorkers}");
        _output.WriteLine($"Chunk size: {config.ChunkSizeBytes / 1024} KB");
        _output.WriteLine($"GC sample interval: {config.GcSampleInterval}");
        _output.WriteLine($"Gen2 threshold: {config.Gen2ThresholdPerMinute}/min");
        _output.WriteLine($"Max working set growth: {config.MaxWorkingSetGrowthPercent}%");
        _output.WriteLine("");

        var workerExceptions = new ConcurrentBag<Exception>();
        var workerStats = new WorkerStats[config.ConcurrentWorkers];
        for (int i = 0; i < workerStats.Length; i++)
            workerStats[i] = new WorkerStats();

        // Grace period for shutdown: 30 seconds beyond the soak duration
        var gracePeriod = TimeSpan.FromSeconds(30);

        using var gcMonitor = new GcEventMonitor(config.GcSampleInterval);
        using var cts = new CancellationTokenSource(effectiveDuration);

        var stopwatch = Stopwatch.StartNew();

        // Spawn concurrent workers
        var workerTasks = new Task[config.ConcurrentWorkers];
        for (int i = 0; i < config.ConcurrentWorkers; i++)
        {
            int workerIndex = i;
            workerTasks[i] = Task.Run(async () =>
            {
                await RunWorker(
                    workerIndex,
                    config,
                    workerStats[workerIndex],
                    workerExceptions,
                    cts.Token);
            });
        }

        // Wait for all workers to complete (duration + grace)
        var allCompleted = Task.WhenAll(workerTasks);
        var completedInTime = await Task.WhenAny(allCompleted, Task.Delay(effectiveDuration + gracePeriod)) == allCompleted;

        stopwatch.Stop();

        // Capture final GC snapshot
        gcMonitor.CaptureFinalsnapshot();

        // --- Analysis ---
        var gen2Rate = gcMonitor.GetGen2RatePerMinute();
        var workingSetGrowth = gcMonitor.GetWorkingSetGrowthPercent();
        var isMonotonic = gcMonitor.IsWorkingSetMonotonic();
        var samples = gcMonitor.GetSamples();

        // --- Report ---
        _output.WriteLine($"=== Soak Test Results ===");
        _output.WriteLine($"Elapsed: {stopwatch.Elapsed}");
        _output.WriteLine($"All workers completed in time: {completedInTime}");
        _output.WriteLine("");

        // Per-worker stats
        long totalOps = 0, totalWritten = 0, totalRead = 0, totalErrors = 0;
        for (int i = 0; i < workerStats.Length; i++)
        {
            var ws = workerStats[i];
            totalOps += Interlocked.Read(ref ws.OperationsCompleted);
            totalWritten += Interlocked.Read(ref ws.BytesWritten);
            totalRead += Interlocked.Read(ref ws.BytesRead);
            totalErrors += Interlocked.Read(ref ws.Errors);
            _output.WriteLine($"Worker {i}: {Interlocked.Read(ref ws.OperationsCompleted)} ops, " +
                $"{Interlocked.Read(ref ws.BytesWritten) / (1024 * 1024)} MB written, " +
                $"{Interlocked.Read(ref ws.BytesRead) / (1024 * 1024)} MB read, " +
                $"{Interlocked.Read(ref ws.Errors)} errors");
        }
        _output.WriteLine("");
        _output.WriteLine($"Total operations: {totalOps}");
        _output.WriteLine($"Total data written: {totalWritten / (1024 * 1024)} MB");
        _output.WriteLine($"Total data read: {totalRead / (1024 * 1024)} MB");
        _output.WriteLine($"Total errors: {totalErrors}");
        _output.WriteLine("");

        // GC summary
        _output.WriteLine($"=== GC Summary ===");
        _output.WriteLine($"Samples collected: {samples.Count}");
        if (samples.Count > 0)
        {
            var first = samples[0];
            var last = samples[^1];
            _output.WriteLine($"Gen0 collections: {first.Gen0Count} -> {last.Gen0Count} (delta: {last.Gen0Count - first.Gen0Count})");
            _output.WriteLine($"Gen1 collections: {first.Gen1Count} -> {last.Gen1Count} (delta: {last.Gen1Count - first.Gen1Count})");
            _output.WriteLine($"Gen2 collections: {first.Gen2Count} -> {last.Gen2Count} (delta: {last.Gen2Count - first.Gen2Count})");
            _output.WriteLine($"Gen2 rate: {gen2Rate:F2}/min (threshold: {config.Gen2ThresholdPerMinute}/min)");
            _output.WriteLine("");
            _output.WriteLine($"=== Working Set Trend ===");
            _output.WriteLine($"Initial: {first.WorkingSetBytes / (1024 * 1024)} MB");

            long peakWorkingSet = 0;
            foreach (var s in samples)
            {
                if (s.WorkingSetBytes > peakWorkingSet)
                    peakWorkingSet = s.WorkingSetBytes;
            }

            _output.WriteLine($"Peak: {peakWorkingSet / (1024 * 1024)} MB");
            _output.WriteLine($"Final: {last.WorkingSetBytes / (1024 * 1024)} MB");
            _output.WriteLine($"Growth: {workingSetGrowth:F2}% (max: {config.MaxWorkingSetGrowthPercent}%)");
            _output.WriteLine($"Monotonic growth detected: {isMonotonic}");
        }
        _output.WriteLine("");

        // --- Assertions ---
        _output.WriteLine("=== Assertions ===");

        // 1. Gen2 rate below threshold
        var gen2Pass = gen2Rate <= config.Gen2ThresholdPerMinute;
        _output.WriteLine($"Gen2 rate <= {config.Gen2ThresholdPerMinute}/min: {(gen2Pass ? "PASS" : "FAIL")} ({gen2Rate:F2}/min)");

        // 2. Working set growth below threshold
        var growthPass = workingSetGrowth <= config.MaxWorkingSetGrowthPercent;
        _output.WriteLine($"Working set growth <= {config.MaxWorkingSetGrowthPercent}%: {(growthPass ? "PASS" : "FAIL")} ({workingSetGrowth:F2}%)");

        // 3. No monotonic working set growth (potential leak)
        var monotonicPass = !isMonotonic;
        _output.WriteLine($"No monotonic WS growth: {(monotonicPass ? "PASS" : "FAIL")}");

        // 4. No exceptions during workload
        var noExceptions = workerExceptions.IsEmpty;
        _output.WriteLine($"No worker exceptions: {(noExceptions ? "PASS" : "FAIL")} ({workerExceptions.Count} exceptions)");
        if (!noExceptions)
        {
            foreach (var ex in workerExceptions.Take(10))
            {
                _output.WriteLine($"  Exception: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 5. All workers completed before timeout + grace
        _output.WriteLine($"All workers completed in time: {(completedInTime ? "PASS" : "FAIL")}");

        // Assert all criteria
        Assert.True(gen2Pass,
            $"Gen2 collection rate {gen2Rate:F2}/min exceeds threshold {config.Gen2ThresholdPerMinute}/min");
        Assert.True(growthPass,
            $"Working set grew {workingSetGrowth:F2}% exceeding max {config.MaxWorkingSetGrowthPercent}%");
        Assert.True(monotonicPass,
            "Working set shows monotonically increasing trend indicating potential memory leak");
        Assert.True(noExceptions,
            $"{workerExceptions.Count} exceptions occurred during soak test workload");
        Assert.True(completedInTime,
            "Not all workers completed within the soak duration plus grace period (potential deadlock)");
    }

    /// <summary>
    /// Worker loop: generates random chunks, writes to a temp file, reads back, verifies checksum.
    /// Repeats until cancellation is requested.
    /// </summary>
    private static async Task RunWorker(
        int workerIndex,
        SoakTestConfiguration config,
        WorkerStats stats,
        ConcurrentBag<Exception> exceptions,
        CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DataWarehouse_Soak", $"worker_{workerIndex}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var buffer = new byte[config.ChunkSizeBytes];

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Generate random data chunk
                    RandomNumberGenerator.Fill(buffer);
                    var expectedHash = SHA256.HashData(buffer);

                    // Write chunk to temp file (simulates VDE pipeline write)
                    var filePath = Path.Combine(tempDir, $"chunk_{Interlocked.Read(ref stats.OperationsCompleted) % 100:D3}.dat");
                    await File.WriteAllBytesAsync(filePath, buffer, cancellationToken);
                    Interlocked.Add(ref stats.BytesWritten, config.ChunkSizeBytes);

                    // Read back and verify checksum
                    var readBack = await File.ReadAllBytesAsync(filePath, cancellationToken);
                    Interlocked.Add(ref stats.BytesRead, readBack.Length);

                    var actualHash = SHA256.HashData(readBack);
                    if (!expectedHash.AsSpan().SequenceEqual(actualHash))
                    {
                        throw new InvalidOperationException(
                            $"Worker {workerIndex}: Data corruption detected at operation {Interlocked.Read(ref stats.OperationsCompleted)}");
                    }

                    Interlocked.Increment(ref stats.OperationsCompleted);

                    // Simulate realistic access pattern with small delay
                    var delay = Random.Shared.Next(config.MinDelayMs, config.MaxDelayMs + 1);
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Clean shutdown requested
                    break;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref stats.Errors);
                    exceptions.Add(ex);

                    // Continue working despite errors (soak tests tolerate transient failures)
                    if (Interlocked.Read(ref stats.Errors) > 100)
                    {
                        // Too many errors — stop this worker to prevent cascading failures
                        break;
                    }
                }
            }
        }
        finally
        {
            // Clean up temp files
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup during shutdown
            }
        }
    }

    public void Dispose()
    {
        // Cleanup for the parent temp directory if empty
        try
        {
            var parentDir = Path.Combine(Path.GetTempPath(), "DataWarehouse_Soak");
            if (Directory.Exists(parentDir) && Directory.GetDirectories(parentDir).Length == 0)
            {
                Directory.Delete(parentDir);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
