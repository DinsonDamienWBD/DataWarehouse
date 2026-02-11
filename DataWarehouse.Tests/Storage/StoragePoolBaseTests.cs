using DataWarehouse.Kernel.Plugins;
using DataWarehouse.SDK.Contracts;
using FluentAssertions;
using System.Text;
using Xunit;

namespace DataWarehouse.Tests.Storage;

/// <summary>
/// Tests for StoragePoolBase thread-safety and batch operations.
/// Uses InMemoryStoragePlugin as concrete storage provider.
/// </summary>
public class StoragePoolBaseTests
{
    #region Thread Safety Tests

    [Fact]
    public async Task ConcurrentSaveToSameUri_ShouldBeThreadSafe()
    {
        // Arrange
        var pool = new TestStoragePool();
        var provider = new InMemoryStoragePlugin();
        pool.AddProvider(provider);

        var uri = new Uri("memory:///concurrent.txt");
        var tasks = new List<Task<StorageResult>>();

        // Act - 100 concurrent writes to the same URI
        for (int i = 0; i < 100; i++)
        {
            var index = i;
            tasks.Add(pool.SaveAsync(
                uri,
                new MemoryStream(Encoding.UTF8.GetBytes($"data-{index}"))));
        }

        var results = await Task.WhenAll(tasks);

        // Assert - All should succeed without data corruption
        results.Should().AllSatisfy(r => r.Success.Should().BeTrue());

        // Load and verify data is one of the written values
        var stream = await pool.LoadAsync(uri);
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();
        content.Should().StartWith("data-");
    }

    [Fact]
    public async Task ConcurrentSaveToDifferentUris_ShouldNotBlock()
    {
        // Arrange
        var pool = new TestStoragePool();
        var provider = new InMemoryStoragePlugin();
        pool.AddProvider(provider);

        var tasks = new List<Task<StorageResult>>();
        var startTime = DateTime.UtcNow;

        // Act - 50 concurrent writes to different URIs
        for (int i = 0; i < 50; i++)
        {
            var uri = new Uri($"memory:///parallel/{i}.txt");
            tasks.Add(pool.SaveAsync(uri, new MemoryStream(new byte[1000])));
        }

        await Task.WhenAll(tasks);
        var duration = DateTime.UtcNow - startTime;

        // Assert - Should complete quickly (not sequentially blocked)
        tasks.Select(t => t.Result).Should().AllSatisfy(r => r.Success.Should().BeTrue());
        duration.TotalSeconds.Should().BeLessThan(5, "parallel writes should not be blocked by URI locks");
    }

    [Fact]
    public async Task SaveAsync_ShouldHandleStreamPositionCorrectly()
    {
        // Arrange
        var pool = new TestStoragePool();
        var provider = new InMemoryStoragePlugin();
        pool.AddProvider(provider);

        var uri = new Uri("memory:///position.txt");
        var data = "test data for position handling"u8.ToArray();
        var stream = new MemoryStream(data);
        stream.Position = 10; // Start in the middle

        // Act - StoragePoolBase resets position to 0 before writing
        var result = await pool.SaveAsync(uri, stream);

        // Assert - Should save from beginning regardless of initial position
        result.Success.Should().BeTrue();
        var loaded = await provider.LoadAsync(uri);
        using var reader = new StreamReader(loaded);
        var content = await reader.ReadToEndAsync();
        content.Should().Be("test data for position handling");
    }

    #endregion

    #region Batch Operations Tests

    [Fact]
    public async Task SaveBatchAsync_ShouldSaveMultipleItems()
    {
        // Arrange
        var pool = new TestStoragePool();
        var provider = new InMemoryStoragePlugin();
        pool.AddProvider(provider);

        var items = Enumerable.Range(0, 10).Select(i => new BatchSaveItem
        {
            Uri = new Uri($"memory:///batch/{i}.txt"),
            Data = new MemoryStream(Encoding.UTF8.GetBytes($"content-{i}"))
        }).ToList();

        // Act
        var result = await pool.SaveBatchAsync(items);

        // Assert
        result.TotalItems.Should().Be(10);
        result.SuccessCount.Should().Be(10);
        result.FailureCount.Should().Be(0);
        provider.Count.Should().Be(10);
    }

    [Fact]
    public async Task SaveBatchAsync_ShouldReportPartialFailures()
    {
        // Arrange - Use limited storage to cause failures
        var config = new InMemoryStorageConfig { MaxMemoryBytes = 50 };
        var pool = new TestStoragePool();
        var provider = new InMemoryStoragePlugin(config);
        pool.AddProvider(provider);

        var items = new List<BatchSaveItem>
        {
            new() { Uri = new Uri("memory:///small.txt"), Data = new MemoryStream(new byte[10]) },
            new() { Uri = new Uri("memory:///large.txt"), Data = new MemoryStream(new byte[100]) } // Too large
        };

        // Act
        var result = await pool.SaveBatchAsync(items);

        // Assert
        result.TotalItems.Should().Be(2);
        result.SuccessCount.Should().Be(1);
        result.FailureCount.Should().Be(1);
        result.Results.Should().Contain(r => !r.Success && r.Error != null);
    }

    [Fact]
    public async Task DeleteBatchAsync_ShouldDeleteMultipleItems()
    {
        // Arrange
        var pool = new TestStoragePool();
        var provider = new InMemoryStoragePlugin();
        pool.AddProvider(provider);

        // Save items first
        for (int i = 0; i < 10; i++)
        {
            await provider.SaveAsync(new Uri($"memory:///delete/{i}.txt"), new MemoryStream(new byte[10]));
        }
        provider.Count.Should().Be(10);

        var uris = Enumerable.Range(0, 5).Select(i => new Uri($"memory:///delete/{i}.txt")).ToList();

        // Act
        var result = await pool.DeleteBatchAsync(uris);

        // Assert
        result.TotalItems.Should().Be(5);
        result.SuccessCount.Should().Be(5);
        provider.Count.Should().Be(5);
    }

    [Fact]
    public async Task ExistsBatchAsync_ShouldCheckMultipleItems()
    {
        // Arrange
        var pool = new TestStoragePool();
        var provider = new InMemoryStoragePlugin();
        pool.AddProvider(provider);

        await provider.SaveAsync(new Uri("memory:///exists/a.txt"), new MemoryStream(new byte[10]));
        await provider.SaveAsync(new Uri("memory:///exists/b.txt"), new MemoryStream(new byte[10]));

        var uris = new List<Uri>
        {
            new("memory:///exists/a.txt"),
            new("memory:///exists/b.txt"),
            new("memory:///exists/c.txt") // Does not exist
        };

        // Act
        var result = await pool.ExistsBatchAsync(uris);

        // Assert
        result[new Uri("memory:///exists/a.txt")].Should().BeTrue();
        result[new Uri("memory:///exists/b.txt")].Should().BeTrue();
        result[new Uri("memory:///exists/c.txt")].Should().BeFalse();
    }

    [Fact]
    public async Task SaveBatchAsync_ShouldExecuteInParallel()
    {
        // Arrange
        var pool = new TestStoragePool();
        var provider = new InMemoryStoragePlugin();
        pool.AddProvider(provider);

        var items = Enumerable.Range(0, 100).Select(i => new BatchSaveItem
        {
            Uri = new Uri($"memory:///parallel/{i}.txt"),
            Data = new MemoryStream(new byte[100])
        }).ToList();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Act
        var result = await pool.SaveBatchAsync(items);

        stopwatch.Stop();

        // Assert
        result.SuccessCount.Should().Be(100);
        // Parallel execution should be faster than serial
        result.Duration.TotalSeconds.Should().BeLessThan(5);
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task LoadAsync_ShouldReportProviderErrors()
    {
        // Arrange
        var pool = new TestStoragePool();
        var provider = new InMemoryStoragePlugin();
        pool.AddProvider(provider);

        // Act & Assert - Loading non-existent item should throw with details
        var act = async () => await pool.LoadAsync(new Uri("memory:///notfound.txt"));
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task SaveAsync_ShouldHandleCancellation()
    {
        // Arrange
        var pool = new TestStoragePool();
        var provider = new InMemoryStoragePlugin();
        pool.AddProvider(provider);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        var act = async () => await pool.SaveAsync(
            new Uri("memory:///cancelled.txt"),
            new MemoryStream(new byte[10]),
            null,
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region Strategy Tests

    [Fact]
    public async Task MirroredStrategy_ShouldWriteToAllProviders()
    {
        // Arrange
        var pool = new TestStoragePool();
        var provider1 = new InMemoryStoragePlugin();
        var provider2 = new InMemoryStoragePlugin();
        pool.AddProvider(provider1);
        pool.AddProvider(provider2, StorageRole.Mirror);
        pool.SetStrategy(new MirroredStrategy());

        var uri = new Uri("memory:///mirrored.txt");

        // Act
        await pool.SaveAsync(uri, new MemoryStream("mirrored data"u8.ToArray()));

        // Assert - Both providers should have the data
        (await provider1.ExistsAsync(uri)).Should().BeTrue();
        (await provider2.ExistsAsync(uri)).Should().BeTrue();
    }

    [Fact]
    public async Task SimpleStrategy_ShouldWriteToOnlyPrimaryProvider()
    {
        // Arrange
        var pool = new TestStoragePool();
        var provider1 = new InMemoryStoragePlugin();
        var provider2 = new InMemoryStoragePlugin();
        pool.AddProvider(provider1);
        pool.AddProvider(provider2, StorageRole.Mirror);
        pool.SetStrategy(new SimpleStrategy());

        var uri = new Uri("memory:///simple.txt");

        // Act
        await pool.SaveAsync(uri, new MemoryStream("simple data"u8.ToArray()));

        // Assert - Only primary should have the data
        (await provider1.ExistsAsync(uri)).Should().BeTrue();
        (await provider2.ExistsAsync(uri)).Should().BeFalse();
    }

    #endregion

    #region Test Helpers

    /// <summary>
    /// Concrete test implementation of StoragePoolBase.
    /// Uses a counter-based key to allow registering multiple instances
    /// of the same provider type (which share the same plugin Id).
    /// </summary>
    private class TestStoragePool : StoragePoolBase
    {
        private int _providerCounter;

        public override string Id => "test-storage-pool";
        public override string PoolId => "test-pool";

        public override void AddProvider(IStorageProvider provider, StorageRole role = StorageRole.Primary)
        {
            ArgumentNullException.ThrowIfNull(provider);
            var id = $"{(provider as IPlugin)?.Id ?? provider.Scheme}-{Interlocked.Increment(ref _providerCounter)}";
            _providers[id] = (provider, role);
        }

        public override Task StartAsync(CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public override Task StopAsync()
        {
            return Task.CompletedTask;
        }
    }

    #endregion
}
