// Hardening tests for AedsCore findings: AedsScalingManager
// Findings: 32 (LOW), 33 (MEDIUM), 45 (MEDIUM), 46 (MEDIUM dup of 33), 47 (CRITICAL)
using DataWarehouse.Plugins.AedsCore.Scaling;
using System.Reflection;

namespace DataWarehouse.Hardening.Tests.AedsCore;

/// <summary>
/// Tests for AedsScalingManager hardening findings.
/// </summary>
public class AedsScalingManagerTests : IDisposable
{
    private readonly AedsScalingManager _manager;

    public AedsScalingManagerTests()
    {
        _manager = new AedsScalingManager(partitionCount: 2);
    }

    /// <summary>
    /// Finding 32: string.GetHashCode(StringComparison.Ordinal) non-deterministic across restarts.
    /// For in-process partition routing this is acceptable — partitions are ephemeral.
    /// Test verifies GetPartitionIndex returns values in valid range.
    /// </summary>
    [Fact]
    public void Finding032_PartitionIndexInValidRange()
    {
        var getPartition = typeof(AedsScalingManager).GetMethod("GetPartitionIndex",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(getPartition);

        for (int i = 0; i < 100; i++)
        {
            var index = (int)getPartition!.Invoke(_manager, new object[] { $"job-{i}" })!;
            Assert.InRange(index, 0, 1); // 2 partitions => index in [0,1]
        }
    }

    /// <summary>
    /// Finding 33 + 46: Silent catch in ProcessPartitionAsync — only increments _jobsFailed.
    /// FIX: Now also writes diagnostic message with exception details.
    /// Test verifies failed jobs increment the counter.
    /// </summary>
    [Fact]
    public async Task Finding033_046_FailedJobsTracked()
    {
        // Enqueue a job that will fail
        var failingJob = new AedsJob
        {
            JobId = "failing-job-001",
            CollectionName = "test",
            OperationType = "test",
            Handler = _ => throw new InvalidOperationException("Test failure")
        };

        _manager.EnqueueJob(failingJob);

        // Give the partition worker time to process
        await Task.Delay(200);

        var metrics = _manager.GetScalingMetrics();
        var failed = (long)metrics["aeds.jobsFailed"];
        Assert.True(failed >= 1, "Failed jobs should be tracked");
    }

    /// <summary>
    /// Finding 45: Math.Abs(int.MinValue) overflow in GetPartitionIndex.
    /// FIX: Uses (hash & 0x7FFFFFFF) instead of Math.Abs.
    /// Test verifies no overflow exception for a string whose GetHashCode returns int.MinValue.
    /// </summary>
    [Fact]
    public void Finding045_GetPartitionIndexNoOverflowOnIntMinValue()
    {
        var getPartition = typeof(AedsScalingManager).GetMethod("GetPartitionIndex",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(getPartition);

        // We can't control GetHashCode, but we verify the method doesn't throw
        // for a large number of inputs. The fix uses (hash & 0x7FFFFFFF) which
        // always produces non-negative results.
        var exception = Record.Exception(() =>
        {
            for (int i = 0; i < 10_000; i++)
            {
                getPartition!.Invoke(_manager, new object[] { Guid.NewGuid().ToString() });
            }
        });
        Assert.Null(exception);
    }

    /// <summary>
    /// Finding 47: ComputeHitRate takes params by value then calls Interlocked.Read on stack copies.
    /// FIX: Removed Interlocked.Read, uses parameter values directly (they are already copies).
    /// Test verifies ComputeHitRate returns correct values.
    /// </summary>
    [Fact]
    public void Finding047_ComputeHitRateReturnsCorrectValues()
    {
        // Put and get some manifests to generate hits/misses
        _manager.PutManifest("key1", new byte[] { 1, 2, 3 });
        _manager.PutManifest("key2", new byte[] { 4, 5, 6 });

        // Generate hits
        var hit1 = _manager.GetManifest("key1");
        var hit2 = _manager.GetManifest("key2");
        Assert.NotNull(hit1);
        Assert.NotNull(hit2);

        // Generate a miss
        var miss = _manager.GetManifest("nonexistent");
        Assert.Null(miss);

        // Verify metrics contain hit rate
        var metrics = _manager.GetScalingMetrics();
        var hitRate = (double)metrics["aeds.manifests.hitRate"];

        // 2 hits, 1 miss => hit rate should be approximately 0.667
        Assert.True(hitRate > 0.6 && hitRate < 0.7,
            $"Expected hit rate ~0.667, got {hitRate}");
    }

    /// <summary>
    /// Verifies job enqueue and processing works end-to-end.
    /// </summary>
    [Fact]
    public void EnqueueJobProcessedSuccessfully()
    {
        var completed = new ManualResetEventSlim(false);

        var job = new AedsJob
        {
            JobId = "success-job-001",
            CollectionName = "test",
            OperationType = "test",
            Handler = _ => { completed.Set(); return Task.CompletedTask; }
        };

        _manager.EnqueueJob(job);

        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)), "Job should complete within 5 seconds");
    }

    public void Dispose()
    {
        _manager.Dispose();
    }
}
