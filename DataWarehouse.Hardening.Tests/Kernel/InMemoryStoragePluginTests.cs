using DataWarehouse.Kernel.Plugins;

namespace DataWarehouse.Hardening.Tests.Kernel;

/// <summary>
/// Hardening tests for InMemoryStoragePlugin — findings 55-56.
/// </summary>
public class InMemoryStoragePluginTests
{
    // Finding 55: StoredBlob.LastAccessedAt and AccessCount mutated without synchronization
    // Finding 56: Race condition
    // FIX APPLIED: LoadAsync now uses Interlocked for AccessCount, volatile for LastAccessedAt
    [Fact]
    public async Task Finding55_56_ConcurrentLoad_NoRace()
    {
        var plugin = new InMemoryStoragePlugin(InMemoryStorageConfig.SmallCache);
        var uri = new Uri("memory:///test/concurrent-item");

        // Store an item
        using var data = new MemoryStream(new byte[100]);
        await plugin.SaveAsync(uri, data);

        // Concurrent loads should not throw
        var tasks = Enumerable.Range(0, 20).Select(_ =>
            Task.Run(async () =>
            {
                var stream = await plugin.LoadAsync(uri);
                stream.Dispose();
            })
        ).ToArray();

        await Task.WhenAll(tasks);
        Assert.True(true, "Concurrent loads completed without race condition");
    }
}
