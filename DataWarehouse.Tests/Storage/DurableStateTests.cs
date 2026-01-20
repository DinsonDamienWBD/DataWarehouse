using DataWarehouse.SDK.Utilities;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Storage;

/// <summary>
/// Tests for DurableState journal-based persistence.
/// Tests thread-safety, compaction, and crash recovery.
/// </summary>
public class DurableStateTests : IDisposable
{
    private readonly string _testDir;
    private readonly List<string> _tempFiles = new();

    public DurableStateTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"DurableStateTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); } catch { }
            try { File.Delete(file + ".compact"); } catch { }
        }
        try { Directory.Delete(_testDir, true); } catch { }
    }

    private string GetTempFilePath()
    {
        var path = Path.Combine(_testDir, $"test_{Guid.NewGuid():N}.journal");
        _tempFiles.Add(path);
        return path;
    }

    #region Basic Operations

    [Fact]
    public void Set_ShouldStoreValue()
    {
        // Arrange
        using var state = new DurableState<string>(GetTempFilePath());

        // Act
        state.Set("key1", "value1");

        // Assert
        state.Get("key1").Should().Be("value1");
    }

    [Fact]
    public void Remove_ShouldDeleteValue()
    {
        // Arrange
        using var state = new DurableState<string>(GetTempFilePath());
        state.Set("key1", "value1");

        // Act
        var result = state.Remove("key1", out var removed);

        // Assert
        result.Should().BeTrue();
        removed.Should().Be("value1");
        state.Get("key1").Should().BeNull();
    }

    [Fact]
    public void TryGet_ShouldReturnFalseForMissingKey()
    {
        // Arrange
        using var state = new DurableState<string>(GetTempFilePath());

        // Act
        var result = state.TryGet("nonexistent", out var value);

        // Assert
        result.Should().BeFalse();
        value.Should().BeNull();
    }

    [Fact]
    public void GetAll_ShouldReturnAllValues()
    {
        // Arrange
        using var state = new DurableState<int>(GetTempFilePath());
        state.Set("a", 1);
        state.Set("b", 2);
        state.Set("c", 3);

        // Act
        var values = state.GetAll().ToList();

        // Assert
        values.Should().BeEquivalentTo([1, 2, 3]);
    }

    [Fact]
    public void GetAllKeyValues_ShouldReturnAllPairs()
    {
        // Arrange
        using var state = new DurableState<string>(GetTempFilePath());
        state.Set("key1", "value1");
        state.Set("key2", "value2");

        // Act
        var pairs = state.GetAllKeyValues().ToList();

        // Assert
        pairs.Should().HaveCount(2);
        pairs.Should().Contain(p => p.Key == "key1" && p.Value == "value1");
        pairs.Should().Contain(p => p.Key == "key2" && p.Value == "value2");
    }

    #endregion

    #region Persistence and Recovery

    [Fact]
    public void State_ShouldPersistAcrossRestarts()
    {
        // Arrange
        var filePath = GetTempFilePath();

        // Act - Create state, write data, dispose
        using (var state = new DurableState<string>(filePath))
        {
            state.Set("persistent", "data");
        }

        // Reopen and verify
        using (var state2 = new DurableState<string>(filePath))
        {
            // Assert
            state2.Get("persistent").Should().Be("data");
        }
    }

    [Fact]
    public void State_ShouldReplayJournalCorrectly()
    {
        // Arrange
        var filePath = GetTempFilePath();

        // Act - Multiple operations
        using (var state = new DurableState<int>(filePath))
        {
            state.Set("a", 1);
            state.Set("b", 2);
            state.Set("a", 10); // Update
            state.Remove("b", out _);
            state.Set("c", 3);
        }

        // Reopen and verify replay
        using (var state2 = new DurableState<int>(filePath))
        {
            // Assert
            state2.Get("a").Should().Be(10);
            state2.TryGet("b", out _).Should().BeFalse();
            state2.Get("c").Should().Be(3);
        }
    }

    [Fact]
    public void State_ShouldHandleComplexTypes()
    {
        // Arrange
        var filePath = GetTempFilePath();
        var testData = new TestComplexType
        {
            Id = 123,
            Name = "Test",
            Tags = ["tag1", "tag2"],
            Nested = new NestedType { Value = 42 }
        };

        // Act
        using (var state = new DurableState<TestComplexType>(filePath))
        {
            state.Set("complex", testData);
        }

        using (var state2 = new DurableState<TestComplexType>(filePath))
        {
            // Assert
            var loaded = state2.Get("complex");
            loaded.Should().NotBeNull();
            loaded!.Id.Should().Be(123);
            loaded.Name.Should().Be("Test");
            loaded.Tags.Should().BeEquivalentTo(["tag1", "tag2"]);
            loaded.Nested!.Value.Should().Be(42);
        }
    }

    #endregion

    #region Compaction Tests

    [Fact]
    public void Compact_ShouldReduceFileSize()
    {
        // Arrange
        var filePath = GetTempFilePath();

        using (var state = new DurableState<string>(filePath))
        {
            // Create lots of updates to same key
            for (int i = 0; i < 1000; i++)
            {
                state.Set("key", $"value-{i}");
            }
        }

        var sizeBeforeCompact = new FileInfo(filePath).Length;

        // Act
        using (var state = new DurableState<string>(filePath))
        {
            state.Compact();
        }

        var sizeAfterCompact = new FileInfo(filePath).Length;

        // Assert - Should be much smaller after compaction
        sizeAfterCompact.Should().BeLessThan(sizeBeforeCompact / 2);

        // Verify data is still intact
        using (var state = new DurableState<string>(filePath))
        {
            state.Get("key").Should().Be("value-999");
        }
    }

    [Fact]
    public void Compact_ShouldBeThreadSafe()
    {
        // Arrange
        var filePath = GetTempFilePath();
        using var state = new DurableState<int>(filePath);

        // Pre-populate
        for (int i = 0; i < 100; i++)
        {
            state.Set($"key{i}", i);
        }

        // Act - Concurrent compaction and writes
        var tasks = new List<Task>();

        tasks.Add(Task.Run(() => state.Compact()));

        for (int i = 0; i < 50; i++)
        {
            var index = i;
            tasks.Add(Task.Run(() => state.Set($"concurrent{index}", index)));
        }

        // Assert - Should not throw
        var act = async () => await Task.WhenAll(tasks);
        act.Should().NotThrowAsync();
    }

    [Fact]
    public void Compact_ShouldPreserveAllData()
    {
        // Arrange
        var filePath = GetTempFilePath();

        using (var state = new DurableState<string>(filePath))
        {
            state.Set("a", "apple");
            state.Set("b", "banana");
            state.Set("c", "cherry");
            state.Remove("b", out _);

            // Act
            state.Compact();

            // Assert - Check immediately
            state.Get("a").Should().Be("apple");
            state.TryGet("b", out _).Should().BeFalse();
            state.Get("c").Should().Be("cherry");
        }

        // Assert - Check after reopen
        using (var state2 = new DurableState<string>(filePath))
        {
            state2.Get("a").Should().Be("apple");
            state2.TryGet("b", out _).Should().BeFalse();
            state2.Get("c").Should().Be("cherry");
        }
    }

    [Fact]
    public void AutoCompaction_ShouldTriggerAtThreshold()
    {
        // Arrange
        var filePath = GetTempFilePath();

        using (var state = new DurableState<int>(filePath))
        {
            // Write more than CompactionThreshold (5000) operations
            for (int i = 0; i < 5001; i++)
            {
                state.Set("key", i);
            }
        }

        // After auto-compaction, file should be small
        var fileSize = new FileInfo(filePath).Length;
        fileSize.Should().BeLessThan(1000); // Just one entry after compaction

        // Verify data integrity
        using (var state2 = new DurableState<int>(filePath))
        {
            state2.Get("key").Should().Be(5000);
        }
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public void ConcurrentSetAndGet_ShouldBeThreadSafe()
    {
        // Arrange
        var filePath = GetTempFilePath();
        using var state = new DurableState<int>(filePath);

        var tasks = new List<Task>();
        var exceptions = new List<Exception>();

        // Act - Concurrent reads and writes
        for (int i = 0; i < 100; i++)
        {
            var index = i;
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    state.Set($"key{index}", index);
                    var _ = state.Get($"key{index}");
                }
                catch (Exception ex)
                {
                    lock (exceptions) { exceptions.Add(ex); }
                }
            }));
        }

        Task.WhenAll(tasks).Wait();

        // Assert
        exceptions.Should().BeEmpty();
    }

    [Fact]
    public void ConcurrentSetToSameKey_ShouldBeThreadSafe()
    {
        // Arrange
        var filePath = GetTempFilePath();
        using var state = new DurableState<int>(filePath);

        var tasks = new List<Task>();

        // Act - All threads write to the same key
        for (int i = 0; i < 100; i++)
        {
            var index = i;
            tasks.Add(Task.Run(() => state.Set("shared", index)));
        }

        Task.WhenAll(tasks).Wait();

        // Assert - Should have one of the values, no corruption
        var value = state.Get("shared");
        value.Should().BeInRange(0, 99);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void State_ShouldHandleEmptyFile()
    {
        // Arrange
        var filePath = GetTempFilePath();

        // Act
        using var state = new DurableState<string>(filePath);

        // Assert
        state.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void State_ShouldHandleSpecialCharactersInKeys()
    {
        // Arrange
        var filePath = GetTempFilePath();
        using var state = new DurableState<string>(filePath);

        // Act
        state.Set("key/with/slashes", "value1");
        state.Set("key with spaces", "value2");
        state.Set("key:with:colons", "value3");

        // Assert
        state.Get("key/with/slashes").Should().Be("value1");
        state.Get("key with spaces").Should().Be("value2");
        state.Get("key:with:colons").Should().Be("value3");
    }

    [Fact]
    public void State_ShouldHandleNullValues()
    {
        // Arrange
        var filePath = GetTempFilePath();
        using var state = new DurableState<string?>(filePath);

        // Act & Assert - setting null effectively removes the key behavior
        state.Set("nullable", null);
        state.TryGet("nullable", out var value).Should().BeFalse();
    }

    #endregion

    #region Test Helpers

    private class TestComplexType
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();
        public NestedType? Nested { get; set; }
    }

    private class NestedType
    {
        public int Value { get; set; }
    }

    #endregion
}
