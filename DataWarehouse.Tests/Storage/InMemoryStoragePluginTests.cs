using DataWarehouse.Kernel.Plugins;
using FluentAssertions;
using System.Text;
using Xunit;

namespace DataWarehouse.Tests.Storage;

/// <summary>
/// Comprehensive tests for InMemoryStoragePlugin.
/// Tests thread-safety, LRU eviction, memory limits, and concurrent access.
/// </summary>
public class InMemoryStoragePluginTests
{
    #region Basic Operations

    [Fact]
    public async Task SaveAsync_ShouldStoreDataSuccessfully()
    {
        // Arrange
        var plugin = new InMemoryStoragePlugin();
        var uri = new Uri("memory:///test/file.txt");
        var data = "Hello, World!"u8.ToArray();

        // Act
        await plugin.SaveAsync(uri, new MemoryStream(data));

        // Assert
        plugin.Count.Should().Be(1);
        var loaded = await plugin.LoadAsync(uri);
        using var reader = new StreamReader(loaded);
        var content = await reader.ReadToEndAsync();
        content.Should().Be("Hello, World!");
    }

    [Fact]
    public async Task LoadAsync_ShouldThrowWhenItemNotFound()
    {
        // Arrange
        var plugin = new InMemoryStoragePlugin();
        var uri = new Uri("memory:///nonexistent.txt");

        // Act & Assert
        var act = async () => await plugin.LoadAsync(uri);
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveItem()
    {
        // Arrange
        var plugin = new InMemoryStoragePlugin();
        var uri = new Uri("memory:///test/file.txt");
        await plugin.SaveAsync(uri, new MemoryStream("data"u8.ToArray()));
        plugin.Count.Should().Be(1);

        // Act
        await plugin.DeleteAsync(uri);

        // Assert
        plugin.Count.Should().Be(0);
        var exists = await plugin.ExistsAsync(uri);
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task ExistsAsync_ShouldReturnTrueForExistingItem()
    {
        // Arrange
        var plugin = new InMemoryStoragePlugin();
        var uri = new Uri("memory:///exists.txt");
        await plugin.SaveAsync(uri, new MemoryStream("data"u8.ToArray()));

        // Act & Assert
        (await plugin.ExistsAsync(uri)).Should().BeTrue();
        (await plugin.ExistsAsync(new Uri("memory:///notexists.txt"))).Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_ShouldUpdateExistingItem()
    {
        // Arrange
        var plugin = new InMemoryStoragePlugin();
        var uri = new Uri("memory:///update.txt");
        await plugin.SaveAsync(uri, new MemoryStream("original"u8.ToArray()));

        // Act
        await plugin.SaveAsync(uri, new MemoryStream("updated"u8.ToArray()));

        // Assert
        plugin.Count.Should().Be(1);
        var loaded = await plugin.LoadAsync(uri);
        using var reader = new StreamReader(loaded);
        (await reader.ReadToEndAsync()).Should().Be("updated");
    }

    #endregion

    #region Memory Limits and LRU Eviction

    [Fact]
    public async Task SaveAsync_ShouldEvictLruItemWhenMemoryLimitExceeded()
    {
        // Arrange - 100 byte limit
        var config = new InMemoryStorageConfig { MaxMemoryBytes = 100 };
        var plugin = new InMemoryStoragePlugin(config);

        // Save 50 bytes
        var uri1 = new Uri("memory:///first.txt");
        await plugin.SaveAsync(uri1, new MemoryStream(new byte[50]));

        // Access first item to set its last access time
        await plugin.LoadAsync(uri1);

        // Save another 50 bytes
        var uri2 = new Uri("memory:///second.txt");
        await plugin.SaveAsync(uri2, new MemoryStream(new byte[50]));
        await Task.Delay(10); // Ensure different timestamps

        // Act - Save 60 bytes, should evict LRU item (second)
        var uri3 = new Uri("memory:///third.txt");
        await plugin.SaveAsync(uri3, new MemoryStream(new byte[60]));

        // Assert - second should be evicted (least recently used)
        (await plugin.ExistsAsync(uri1)).Should().BeTrue();
        (await plugin.ExistsAsync(uri2)).Should().BeFalse();
        (await plugin.ExistsAsync(uri3)).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_ShouldEvictWhenItemCountLimitExceeded()
    {
        // Arrange - max 3 items
        var config = new InMemoryStorageConfig { MaxItemCount = 3 };
        var plugin = new InMemoryStoragePlugin(config);

        for (int i = 0; i < 3; i++)
        {
            await plugin.SaveAsync(new Uri($"memory:///item{i}.txt"), new MemoryStream(new byte[10]));
            await Task.Delay(10);
        }

        plugin.Count.Should().Be(3);

        // Act - Save 4th item
        await plugin.SaveAsync(new Uri("memory:///item3.txt"), new MemoryStream(new byte[10]));

        // Assert - item0 should be evicted (LRU)
        plugin.Count.Should().Be(3);
        (await plugin.ExistsAsync(new Uri("memory:///item0.txt"))).Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_ShouldThrowWhenSingleItemExceedsMaxMemory()
    {
        // Arrange
        var config = new InMemoryStorageConfig { MaxMemoryBytes = 100 };
        var plugin = new InMemoryStoragePlugin(config);

        // Act & Assert - try to save 200 bytes
        var act = async () => await plugin.SaveAsync(
            new Uri("memory:///large.txt"),
            new MemoryStream(new byte[200]));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exceeds maximum*");
    }

    [Fact]
    public async Task EvictionCallback_ShouldBeInvokedOnEviction()
    {
        // Arrange
        var config = new InMemoryStorageConfig { MaxItemCount = 1 };
        var plugin = new InMemoryStoragePlugin(config);
        var evictionEvents = new List<EvictionEvent>();

        plugin.OnEviction(evt => evictionEvents.Add(evt));

        await plugin.SaveAsync(new Uri("memory:///first.txt"), new MemoryStream(new byte[10]));

        // Act - Save second item, should evict first
        await plugin.SaveAsync(new Uri("memory:///second.txt"), new MemoryStream(new byte[10]));

        // Assert
        evictionEvents.Should().HaveCount(1);
        evictionEvents[0].Key.Should().Be("first.txt");
        evictionEvents[0].Reason.Should().Be(EvictionReason.ItemCountLimit);
    }

    [Fact]
    public void EvictLruItems_ShouldEvictSpecifiedCount()
    {
        // Arrange
        var plugin = new InMemoryStoragePlugin();
        for (int i = 0; i < 10; i++)
        {
            plugin.SaveAsync(new Uri($"memory:///item{i}.txt"), new MemoryStream(new byte[10])).Wait();
            Task.Delay(10).Wait();
        }

        // Act
        var evicted = plugin.EvictLruItems(5);

        // Assert
        evicted.Should().Be(5);
        plugin.Count.Should().Be(5);
    }

    [Fact]
    public async Task EvictOlderThan_ShouldEvictExpiredItems()
    {
        // Arrange
        var plugin = new InMemoryStoragePlugin();
        await plugin.SaveAsync(new Uri("memory:///old.txt"), new MemoryStream(new byte[10]));
        await Task.Delay(100);
        await plugin.SaveAsync(new Uri("memory:///new.txt"), new MemoryStream(new byte[10]));

        // Act - Evict items not accessed in last 50ms
        var evicted = plugin.EvictOlderThan(TimeSpan.FromMilliseconds(50));

        // Assert
        evicted.Should().Be(1);
        (await plugin.ExistsAsync(new Uri("memory:///old.txt"))).Should().BeFalse();
        (await plugin.ExistsAsync(new Uri("memory:///new.txt"))).Should().BeTrue();
    }

    #endregion

    #region Thread Safety and Concurrent Access

    [Fact]
    public async Task ConcurrentSave_ShouldBeThreadSafe()
    {
        // Arrange
        var plugin = new InMemoryStoragePlugin();
        var tasks = new List<Task>();
        var itemCount = 100;

        // Act - Save 100 items concurrently
        for (int i = 0; i < itemCount; i++)
        {
            var index = i;
            tasks.Add(Task.Run(async () =>
            {
                var uri = new Uri($"memory:///concurrent/{index}.txt");
                await plugin.SaveAsync(uri, new MemoryStream(Encoding.UTF8.GetBytes($"data-{index}")));
            }));
        }

        await Task.WhenAll(tasks);

        // Assert
        plugin.Count.Should().Be(itemCount);
    }

    [Fact]
    public async Task ConcurrentReadWrite_ShouldBeThreadSafe()
    {
        // Arrange
        var plugin = new InMemoryStoragePlugin();
        var uri = new Uri("memory:///concurrent.txt");
        await plugin.SaveAsync(uri, new MemoryStream("initial"u8.ToArray()));

        var readTasks = new List<Task<string>>();
        var writeTasks = new List<Task>();

        // Act - Concurrent reads and writes
        for (int i = 0; i < 50; i++)
        {
            var index = i;
            writeTasks.Add(Task.Run(async () =>
            {
                await plugin.SaveAsync(uri, new MemoryStream(Encoding.UTF8.GetBytes($"data-{index}")));
            }));

            readTasks.Add(Task.Run(async () =>
            {
                try
                {
                    var stream = await plugin.LoadAsync(uri);
                    using var reader = new StreamReader(stream);
                    return await reader.ReadToEndAsync();
                }
                catch (FileNotFoundException)
                {
                    return "not-found";
                }
            }));
        }

        await Task.WhenAll(writeTasks.Concat(readTasks.Cast<Task>()));

        // Assert - No exceptions thrown, all reads completed
        var results = await Task.WhenAll(readTasks);
        results.Should().AllSatisfy(r => r.Should().NotBeNullOrEmpty());
    }

    [Fact]
    public async Task ConcurrentSaveAndDelete_ShouldBeThreadSafe()
    {
        // Arrange
        var plugin = new InMemoryStoragePlugin();
        var tasks = new List<Task>();

        // Act - Mixed save and delete operations
        for (int i = 0; i < 100; i++)
        {
            var index = i;
            tasks.Add(Task.Run(async () =>
            {
                var uri = new Uri($"memory:///mixed/{index}.txt");
                await plugin.SaveAsync(uri, new MemoryStream(Encoding.UTF8.GetBytes($"data-{index}")));
                if (index % 2 == 0)
                {
                    await plugin.DeleteAsync(uri);
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Assert - Should have ~50 items (odd numbered ones)
        plugin.Count.Should().BeCloseTo(50, 5);
    }

    [Fact]
    public async Task ConcurrentEviction_ShouldBeThreadSafe()
    {
        // Arrange - Very limited storage to force many evictions
        var config = new InMemoryStorageConfig { MaxItemCount = 10 };
        var plugin = new InMemoryStoragePlugin(config);
        var evictionCount = 0;

        plugin.OnEviction(_ => Interlocked.Increment(ref evictionCount));

        var tasks = new List<Task>();

        // Act - Save 100 items concurrently, forcing many evictions
        for (int i = 0; i < 100; i++)
        {
            var index = i;
            tasks.Add(Task.Run(async () =>
            {
                var uri = new Uri($"memory:///evict/{index}.txt");
                await plugin.SaveAsync(uri, new MemoryStream(new byte[10]));
            }));
        }

        await Task.WhenAll(tasks);

        // Assert
        plugin.Count.Should().BeLessOrEqualTo(10);
        evictionCount.Should().BeGreaterOrEqualTo(90);
    }

    #endregion

    #region Listing and Enumeration

    [Fact]
    public async Task ListFilesAsync_ShouldEnumerateAllItems()
    {
        // Arrange
        var plugin = new InMemoryStoragePlugin();
        for (int i = 0; i < 5; i++)
        {
            await plugin.SaveAsync(new Uri($"memory:///list/item{i}.txt"), new MemoryStream(new byte[10]));
        }

        // Act
        var items = new List<DataWarehouse.SDK.Contracts.StorageListItem>();
        await foreach (var item in plugin.ListFilesAsync())
        {
            items.Add(item);
        }

        // Assert
        items.Should().HaveCount(5);
    }

    [Fact]
    public async Task ListFilesAsync_ShouldFilterByPrefix()
    {
        // Arrange
        var plugin = new InMemoryStoragePlugin();
        await plugin.SaveAsync(new Uri("memory:///docs/readme.txt"), new MemoryStream(new byte[10]));
        await plugin.SaveAsync(new Uri("memory:///docs/manual.txt"), new MemoryStream(new byte[10]));
        await plugin.SaveAsync(new Uri("memory:///images/logo.png"), new MemoryStream(new byte[10]));

        // Act
        var docsItems = new List<DataWarehouse.SDK.Contracts.StorageListItem>();
        await foreach (var item in plugin.ListFilesAsync("docs"))
        {
            docsItems.Add(item);
        }

        // Assert
        docsItems.Should().HaveCount(2);
    }

    [Fact]
    public async Task Clear_ShouldRemoveAllItems()
    {
        // Arrange
        var plugin = new InMemoryStoragePlugin();
        for (int i = 0; i < 10; i++)
        {
            await plugin.SaveAsync(new Uri($"memory:///clear/{i}.txt"), new MemoryStream(new byte[10]));
        }

        plugin.Count.Should().Be(10);

        // Act
        plugin.Clear();

        // Assert
        plugin.Count.Should().Be(0);
        plugin.TotalSizeBytes.Should().Be(0);
    }

    #endregion

    #region Statistics and Metrics

    [Fact]
    public async Task GetStats_ShouldReturnAccurateStatistics()
    {
        // Arrange
        var plugin = new InMemoryStoragePlugin();
        await plugin.SaveAsync(new Uri("memory:///stats1.txt"), new MemoryStream(new byte[100]));
        await plugin.SaveAsync(new Uri("memory:///stats2.txt"), new MemoryStream(new byte[200]));

        // Act
        var stats = plugin.GetStats();

        // Assert
        stats.ItemCount.Should().Be(2);
        stats.TotalSizeBytes.Should().Be(300);
        stats.OldestItem.Should().NotBeNull();
        stats.NewestItem.Should().NotBeNull();
    }

    [Fact]
    public async Task MemoryUtilization_ShouldBeAccurate()
    {
        // Arrange
        var config = new InMemoryStorageConfig { MaxMemoryBytes = 1000 };
        var plugin = new InMemoryStoragePlugin(config);

        await plugin.SaveAsync(new Uri("memory:///util.txt"), new MemoryStream(new byte[500]));

        // Assert
        plugin.MemoryUtilization.Should().BeApproximately(50, 1);
        plugin.IsUnderMemoryPressure.Should().BeFalse(); // Default threshold is 80%
    }

    [Fact]
    public async Task IsUnderMemoryPressure_ShouldBeTrueWhenThresholdExceeded()
    {
        // Arrange - 80% threshold, 1000 bytes max
        var config = new InMemoryStorageConfig
        {
            MaxMemoryBytes = 1000,
            MemoryPressureThreshold = 80
        };
        var plugin = new InMemoryStoragePlugin(config);

        // Act - Fill 85%
        await plugin.SaveAsync(new Uri("memory:///pressure.txt"), new MemoryStream(new byte[850]));

        // Assert
        plugin.IsUnderMemoryPressure.Should().BeTrue();
    }

    #endregion
}
