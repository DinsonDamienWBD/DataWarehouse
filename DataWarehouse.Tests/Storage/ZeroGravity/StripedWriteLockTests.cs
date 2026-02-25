using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.VirtualDiskEngine.Concurrency;
using Xunit;

namespace DataWarehouse.Tests.Storage.ZeroGravity;

/// <summary>
/// Tests for striped write lock.
/// Verifies: concurrent access for different keys, serialization for same key,
/// power-of-two requirement, disposal behavior, and write region lifecycle.
/// </summary>
public sealed class StripedWriteLockTests
{
    [Fact]
    public async Task AcquireAsync_DifferentKeys_RunConcurrently()
    {
        await using var stripedLock = new StripedWriteLock(16);

        // Acquire two different keys simultaneously - they should both succeed immediately
        var sw = Stopwatch.StartNew();
        var region1 = await stripedLock.AcquireAsync("key-alpha");
        var region2 = await stripedLock.AcquireAsync("key-beta");
        sw.Stop();

        // Both acquired without blocking (under 50ms total)
        Assert.True(sw.ElapsedMilliseconds < 50,
            $"Acquiring two different keys took {sw.ElapsedMilliseconds}ms, expected < 50ms");

        await region1.DisposeAsync();
        await region2.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_SameKey_Serialized()
    {
        await using var stripedLock = new StripedWriteLock(16);
        bool secondAcquired = false;

        // Acquire the first lock
        var region1 = await stripedLock.AcquireAsync("same-key");

        // Start a task that tries to acquire the same key - it should block
        var acquireTask = Task.Run(async () =>
        {
            var region2 = await stripedLock.AcquireAsync("same-key");
            secondAcquired = true;
            await region2.DisposeAsync();
        });

        // Give the task a moment to attempt acquisition
        await Task.Delay(100);
        Assert.False(secondAcquired, "Second acquire should be blocked while first is held");

        // Release first lock
        await region1.DisposeAsync();

        // Now the second acquire should complete
        await acquireTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(secondAcquired, "Second acquire should have succeeded after first was released");
    }

    [Fact]
    public async Task StripeCount_MustBePowerOfTwo()
    {
        Assert.Throws<ArgumentException>(() => new StripedWriteLock(7));
        Assert.Throws<ArgumentException>(() => new StripedWriteLock(0));
        Assert.Throws<ArgumentException>(() => new StripedWriteLock(-1));

        // These should not throw (valid power-of-two values)
        var lock2 = new StripedWriteLock(2);
        Assert.Equal(2, lock2.StripeCount);
        await lock2.DisposeAsync();

        var lock4 = new StripedWriteLock(4);
        Assert.Equal(4, lock4.StripeCount);
        await lock4.DisposeAsync();

        var lock64 = new StripedWriteLock(64);
        Assert.Equal(64, lock64.StripeCount);
        await lock64.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_ReleasesAllStripes()
    {
        var stripedLock = new StripedWriteLock(4);
        await stripedLock.DisposeAsync();

        // After disposal, attempting to acquire should throw ObjectDisposedException
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await stripedLock.AcquireAsync("any-key"));
    }

    [Fact]
    public async Task WriteRegion_DisposalReleasesLock()
    {
        await using var stripedLock = new StripedWriteLock(8);

        // Acquire, then dispose the region
        var region = await stripedLock.AcquireAsync("release-test");
        await region.DisposeAsync();

        // Acquiring the same key again should succeed immediately (not blocked)
        var sw = Stopwatch.StartNew();
        var region2 = await stripedLock.AcquireAsync("release-test");
        sw.Stop();
        await region2.DisposeAsync();

        // Should complete nearly instantly (under 100ms), proving the lock was released
        Assert.True(sw.ElapsedMilliseconds < 100,
            $"Re-acquire took {sw.ElapsedMilliseconds}ms, expected < 100ms");
    }
}
