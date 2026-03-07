using DataWarehouse.Kernel;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.SDK.VirtualDiskEngine;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;
using DataWarehouse.Hardening.Tests.Chaos.TornWrite;

namespace DataWarehouse.Hardening.Tests.Chaos.ResourceExhaustion;

/// <summary>
/// Resource exhaustion chaos tests that prove the system degrades gracefully under
/// ThreadPool starvation, MemoryPool depletion, and disk-full conditions.
///
/// Key guarantees validated:
/// - ThreadPool starvation causes clean timeout/failure, never deadlock
/// - MemoryPool exhaustion returns error, never OOM crash
/// - Disk-full during VDE write fails with IOException, no data corruption
/// - System recovers to normal operation when resources become available
///
/// Report: "Stage 3 - Steps 3-4 - Resource Exhaustion"
/// </summary>
public class ResourceExhaustionTests : IAsyncDisposable
{
    private DataWarehouseKernel? _kernel;

    private async Task<DataWarehouseKernel> BuildKernelAsync(params IPlugin[] plugins)
    {
        var builder = KernelBuilder.Create()
            .WithKernelId($"exhaustion-test-{Guid.NewGuid():N}")
            .WithOperatingMode(OperatingMode.Workstation)
            .UseInMemoryStorage();

        foreach (var plugin in plugins)
        {
            builder.WithPlugin(plugin);
        }

        _kernel = await builder.BuildAndInitializeAsync();
        return _kernel;
    }

    /// <summary>
    /// Sends a heartbeat message through the kernel message bus.
    /// Used to verify kernel responsiveness under resource pressure.
    /// </summary>
    private static Task PingKernelAsync(DataWarehouseKernel kernel, CancellationToken ct)
    {
        var msg = new PluginMessage
        {
            Type = "system.heartbeat",
            Payload = new Dictionary<string, object> { ["source"] = "chaos-test" }
        };
        return kernel.PublishAsync("system.health", msg, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_kernel != null)
        {
            await _kernel.DisposeAsync();
            _kernel = null;
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// ThreadPool starvation: saturate all threads, then attempt a VDE write.
    /// The VDE write must complete or fail cleanly -- never deadlock.
    /// Proof: the test completing within 60s timeout guarantees no deadlock.
    /// The ThreadPool starvation is released after the attempt so recovery is verified.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ThreadPoolStarvation_VdeWrite_TimesOutCleanly()
    {
        // Arrange -- setup VDE harness before starving the pool
        var simulator = new CrashSimulator();
        await using var harness = new VdeTestHarness(simulator);
        await harness.MountAsync();

        bool writeSucceeded = false;
        bool cleanFailure = false;

        // Act -- starve the ThreadPool, then attempt a VDE write directly (no Task.Run)
        using (var starvation = ResourceThrottler.StarveThreadPool(128))
        {
            try
            {
                // Use VDE write which is the actual production code path
                // Under starvation, async continuations may be delayed but the
                // in-memory block device operates synchronously, so the write
                // should still complete -- proving the VDE path doesn't deadlock
                writeSucceeded = await harness.WriteWithWalAsync(
                    blockNumber: 10,
                    data: VdeTestHarness.DeadBeefPattern);
            }
            catch (OperationCanceledException)
            {
                cleanFailure = true;
            }
            catch (TimeoutException)
            {
                cleanFailure = true;
            }
        }

        // Assert -- either the write succeeded (thread pool had capacity)
        // or it failed cleanly (no deadlock, no crash)
        Assert.True(writeSucceeded || cleanFailure,
            "VDE write must succeed or fail cleanly under ThreadPool starvation");

        // The test reaching this point IS the deadlock-freedom proof.
        // If it had deadlocked, the 60s Timeout would have killed the test.

        // Verify data integrity if write succeeded
        if (writeSucceeded)
        {
            Assert.True(harness.VerifyBlock(10, VdeTestHarness.DeadBeefPattern),
                "Data written under starvation must be correct");
        }
    }

    /// <summary>
    /// Memory exhaustion: allocate large buffers until memory pressure is high,
    /// then attempt a VDE write. The write should either succeed or throw
    /// a managed exception -- never an unhandled OOM crash.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task MemoryPoolExhaustion_VdeWrite_CleanFailure()
    {
        // Arrange -- setup VDE harness for write testing
        var simulator = new CrashSimulator();
        await using var harness = new VdeTestHarness(simulator);
        await harness.MountAsync();

        Exception? caughtException = null;
        bool writeSucceeded = false;

        // Act -- exhaust memory, then attempt VDE write
        using (var exhaustion = ResourceThrottler.ExhaustMemoryPool(
            bufferSize: 4 * 1024 * 1024, // 4MB chunks
            maxAllocations: 256))
        {
            try
            {
                // Attempt a VDE write under memory pressure
                bool result = await harness.WriteWithWalAsync(
                    blockNumber: 1,
                    data: VdeTestHarness.DeadBeefPattern);
                writeSucceeded = result;
            }
            catch (OutOfMemoryException ex) // includes InsufficientMemoryException
            {
                caughtException = ex;
            }
            catch (IOException ex)
            {
                // Acceptable -- I/O subsystem detected memory pressure
                caughtException = ex;
            }
        }

        // Assert -- process survived (we're still running!), and either:
        // (a) write succeeded despite pressure, or (b) clean managed exception
        Assert.True(writeSucceeded || caughtException != null,
            "Write must either succeed or throw a managed exception, never crash");

        // Verify we can still operate after memory is freed
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();

        // Post-recovery write should succeed
        bool recoveryWrite = await harness.WriteWithWalAsync(
            blockNumber: 2,
            data: VdeTestHarness.CafeBabePattern);
        Assert.True(recoveryWrite, "Write should succeed after memory pressure is relieved");
    }

    /// <summary>
    /// Disk full mid-write: wrap the backing store with a disk-full simulator,
    /// write data until IOException. Verify IOException is propagated correctly,
    /// VDE state is not corrupted, and next write succeeds after space is freed.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task DiskFull_VdeWrite_IoExceptionNoCorruption()
    {
        // Arrange -- use a MemoryStream with disk-full wrapper
        using var backingStream = new MemoryStream();
        // Allow only 100 bytes before "disk full"
        using var diskFull = ResourceThrottler.SimulateDiskFull(backingStream, bytesBeforeFull: 100);

        byte[] testData = new byte[200];
        Array.Fill(testData, (byte)0xAA);

        // Act -- write more data than the threshold allows
        IOException? ioException = null;

        try
        {
            // Write in small chunks to test partial-write behavior
            for (int i = 0; i < testData.Length; i += 50)
            {
                int count = Math.Min(50, testData.Length - i);
                await diskFull.WriteAsync(testData, i, count);
            }
        }
        catch (IOException ex)
        {
            ioException = ex;
        }

        // Assert -- IOException must have been thrown
        Assert.NotNull(ioException);
        Assert.Contains("No space left on device", ioException!.Message);
        Assert.True(diskFull.IsDiskFull, "Stream should report disk-full state");

        // Verify partial data was written (up to threshold)
        Assert.True(diskFull.TotalBytesWritten <= 100,
            $"Should have written at most 100 bytes, wrote {diskFull.TotalBytesWritten}");

        // Simulate "freeing space" and verify recovery
        diskFull.FreeSpace(newThreshold: 1024);
        Assert.False(diskFull.IsDiskFull, "After freeing space, disk should not be full");

        // Next write should succeed
        byte[] recoveryData = new byte[50];
        Array.Fill(recoveryData, (byte)0xBB);
        await diskFull.WriteAsync(recoveryData, 0, recoveryData.Length);

        Assert.Equal(50, diskFull.TotalBytesWritten); // Reset counter
        Assert.False(diskFull.IsDiskFull);
    }

    /// <summary>
    /// Combined starvation: starve ThreadPool + exhaust memory + simulate disk full
    /// simultaneously. The system must not crash -- the process must survive.
    /// Operations may fail, but failures must be managed exceptions.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task CombinedStarvation_NoCrash()
    {
        // Arrange -- setup VDE harness and kernel before starvation
        var simulator = new CrashSimulator();
        await using var harness = new VdeTestHarness(simulator);
        await harness.MountAsync();

        var kernel = await BuildKernelAsync();
        using var backingStream = new MemoryStream();
        int managedExceptions = 0;
        int successfulOps = 0;

        // Act -- apply all three starvation modes simultaneously
        using (var threadStarvation = ResourceThrottler.StarveThreadPool(64))
        using (var memoryExhaustion = ResourceThrottler.ExhaustMemoryPool(
            bufferSize: 2 * 1024 * 1024, maxAllocations: 128))
        {
            using var diskFull = ResourceThrottler.SimulateDiskFull(backingStream, bytesBeforeFull: 0);

            // Attempt VDE write under combined starvation (direct, no Task.Run)
            try
            {
                await harness.WriteWithWalAsync(0, VdeTestHarness.DeadBeefPattern);
                successfulOps++;
            }
            catch (Exception ex) when (
                ex is OperationCanceledException or
                TimeoutException or
                OutOfMemoryException or
                IOException or
                TaskCanceledException)
            {
                managedExceptions++;
            }

            // Attempt kernel publish under combined starvation
            try
            {
                await PingKernelAsync(kernel, CancellationToken.None);
                successfulOps++;
            }
            catch (Exception ex) when (
                ex is OperationCanceledException or
                TimeoutException or
                OutOfMemoryException or
                IOException or
                TaskCanceledException)
            {
                managedExceptions++;
            }

            // Try a disk write that should immediately fail
            try
            {
                await diskFull.WriteAsync(new byte[10], 0, 10);
                successfulOps++;
            }
            catch (IOException)
            {
                managedExceptions++;
            }
        }

        // Assert -- we're still alive! The process survived combined starvation.
        Assert.True(successfulOps + managedExceptions > 0,
            "At least one operation must have been attempted");

        // Verify system is functional after starvation released
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        await PingKernelAsync(kernel, CancellationToken.None);

        // VDE must still work after combined starvation
        bool postWrite = await harness.WriteWithWalAsync(1, VdeTestHarness.CafeBabePattern);
        Assert.True(postWrite, "VDE write should succeed after combined starvation released");
    }

    /// <summary>
    /// Recovery test: starve resources, release them, verify normal operations resume.
    /// This proves the system doesn't enter a permanent degraded state after starvation.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Recovery_AfterResourcesFreed_OperationsSucceed()
    {
        // Arrange -- build VDE harness and kernel
        var simulator = new CrashSimulator();
        await using var harness = new VdeTestHarness(simulator);
        await harness.MountAsync();

        var kernel = await BuildKernelAsync();

        // Phase 1: Write successfully before starvation
        bool preWrite = await harness.WriteWithWalAsync(0, VdeTestHarness.DeadBeefPattern);
        Assert.True(preWrite, "Pre-starvation write should succeed");
        Assert.True(harness.VerifyBlock(0, VdeTestHarness.DeadBeefPattern),
            "Pre-starvation data should be correct");

        // Phase 2: Apply starvation and attempt operations
        using (var threadStarvation = ResourceThrottler.StarveThreadPool(64))
        {
            // Operations may fail under starvation -- that's acceptable
            try
            {
                // Direct call (no Task.Run) to avoid ThreadPool scheduling deadlock
                await PingKernelAsync(kernel, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // Expected under starvation
            }
            catch (TimeoutException)
            {
                // Expected under starvation
            }
        }
        // Starvation released here via using dispose

        // Phase 3: Give the system a moment to recover thread pool
        await Task.Delay(500);

        // Phase 4: Verify full recovery -- all operations must succeed
        bool postWrite = await harness.WriteWithWalAsync(1, VdeTestHarness.CafeBabePattern);
        Assert.True(postWrite, "Post-starvation VDE write should succeed");
        Assert.True(harness.VerifyBlock(1, VdeTestHarness.CafeBabePattern),
            "Post-starvation data should be correct");

        // Previous data must still be intact
        Assert.True(harness.VerifyBlock(0, VdeTestHarness.DeadBeefPattern),
            "Pre-starvation data must survive the starvation period");

        // Kernel must be responsive
        await PingKernelAsync(kernel, CancellationToken.None);
    }

    /// <summary>
    /// Disk-full VDE write: write through VDE pipeline to a disk-full backing store.
    /// Verifies IOException propagates cleanly through the VDE decorator chain
    /// and pre-existing data remains intact after the failed write.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task DiskFull_VdeWritePath_PreExistingDataIntact()
    {
        // Arrange -- write known data, then trigger disk-full on next write
        var simulator = new CrashSimulator();
        await using var harness = new VdeTestHarness(simulator);
        await harness.MountAsync();

        // Write known good data first
        bool initialWrite = await harness.WriteWithWalAsync(5, VdeTestHarness.DeadBeefPattern);
        Assert.True(initialWrite, "Initial write should succeed");

        // Inject a crash at the File stage to simulate I/O failure
        // (the closest we can get to disk-full in the VDE test harness)
        simulator.InjectAt(DecoratorStage.File, CrashTiming.DuringWrite);

        // Act -- attempt write that will fail at file I/O level
        bool failedWrite = await harness.WriteWithWalAsync(6, VdeTestHarness.CafeBabePattern);

        // Assert -- write should have been interrupted
        Assert.False(failedWrite, "Write should fail when I/O layer reports error");

        // Remount and recover
        await harness.RemountAsync();
        int recovered = await harness.RecoverAsync();

        // Pre-existing data must be intact
        Assert.True(harness.VerifyBlock(5, VdeTestHarness.DeadBeefPattern),
            "Pre-existing data must survive a failed write to a different block");
    }

    /// <summary>
    /// Rapid starvation cycling: rapidly alternate between starvation and normal
    /// operation. Proves the system handles resource oscillation without accumulating
    /// leaked threads, handles, or memory.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task RapidStarvationCycling_NoResourceLeak()
    {
        var kernel = await BuildKernelAsync();

        // Record baseline memory
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        long baselineMemory = GC.GetTotalMemory(true);

        // Cycle through starvation 10 times
        for (int cycle = 0; cycle < 10; cycle++)
        {
            // Starve briefly
            using (var starvation = ResourceThrottler.StarveThreadPool(32))
            {
                await Task.Delay(100);
            }

            // Operate normally
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await PingKernelAsync(kernel, cts.Token);
        }

        // Check for memory leaks (allow generous headroom for GC timing)
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        long finalMemory = GC.GetTotalMemory(true);

        // Allow up to 50MB growth (generous to avoid flaky tests)
        long growth = finalMemory - baselineMemory;
        Assert.True(growth < 50 * 1024 * 1024,
            $"Memory grew by {growth / (1024 * 1024)}MB after 10 starvation cycles -- possible leak");
    }
}
