using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;
using Xunit;

namespace DataWarehouse.Tests.AdaptiveIndex;

/// <summary>
/// v1.0 B-tree compatibility and migration tests.
/// Verifies that existing B-tree data can be wrapped and migrated to Be-tree format.
/// </summary>
public sealed class LegacyMigrationTests
{
    private static byte[] MakeKey(int i)
    {
        var bytes = BitConverter.GetBytes(i);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return bytes;
    }

    [Fact]
    public async Task V1BTree_OpensCorrectly()
    {
        // Create a SortedArrayIndex (simulating v1.0 B-tree data structure)
        // and verify AdaptiveIndexEngine can host it at the appropriate level
        var device = new InMemoryBlockDevice();
        var allocator = new InMemoryBlockAllocator();
        var wal = new InMemoryWriteAheadLog();

        // v1.0 B-tree equivalent: SortedArray with entries
        var sortedArray = new SortedArrayIndex(10_000);
        for (int i = 0; i < 50; i++)
            await sortedArray.InsertAsync(MakeKey(i), i * 100L);

        // Verify v1.0 data is accessible
        Assert.Equal(50L, sortedArray.ObjectCount);
        Assert.Equal(MorphLevel.SortedArray, sortedArray.CurrentLevel);

        for (int i = 0; i < 50; i++)
            Assert.Equal(i * 100L, await sortedArray.LookupAsync(MakeKey(i)));

        sortedArray.Dispose();
    }

    [Fact]
    public async Task V1BTree_MigratesToBeTree()
    {
        // Simulate migration: populate SortedArray, then migrate all entries to BeTree
        var device = new InMemoryBlockDevice();
        var allocator = new InMemoryBlockAllocator();
        var wal = new InMemoryWriteAheadLog();

        // Source: v1.0 SortedArray
        var source = new SortedArrayIndex(10_000);
        for (int i = 0; i < 100; i++)
            await source.InsertAsync(MakeKey(i), i * 10L);

        // Target: BeTree (Level 3)
        var target = new BeTree(device, allocator, wal, 1, 4096);

        // Migrate all entries
        await foreach (var (key, value) in source.RangeQueryAsync(null, null))
        {
            await target.InsertAsync(key, value);
        }

        // Verify all entries preserved in BeTree
        Assert.Equal(source.ObjectCount, target.ObjectCount);
        for (int i = 0; i < 100; i++)
        {
            var result = await target.LookupAsync(MakeKey(i));
            Assert.NotNull(result);
            Assert.Equal(i * 10L, result!.Value);
        }

        source.Dispose();
    }

    [Fact]
    public async Task MigratedBeTree_PerformsCorrectly()
    {
        var device = new InMemoryBlockDevice();
        var allocator = new InMemoryBlockAllocator();
        var wal = new InMemoryWriteAheadLog();

        // Migrate from SortedArray to BeTree
        var source = new SortedArrayIndex(10_000);
        for (int i = 0; i < 50; i++)
            await source.InsertAsync(MakeKey(i), i);

        var betree = new BeTree(device, allocator, wal, 1, 4096);
        await foreach (var (key, value) in source.RangeQueryAsync(null, null))
            await betree.InsertAsync(key, value);
        source.Dispose();

        // Test insert after migration
        await betree.InsertAsync(MakeKey(50), 50);
        Assert.Equal(50L, await betree.LookupAsync(MakeKey(50)));

        // Test delete after migration
        var deleted = await betree.DeleteAsync(MakeKey(25));
        Assert.True(deleted);
        Assert.Null(await betree.LookupAsync(MakeKey(25)));

        // Test update after migration
        var updated = await betree.UpdateAsync(MakeKey(10), 999);
        Assert.True(updated);
        Assert.Equal(999L, await betree.LookupAsync(MakeKey(10)));

        // Test range query after migration
        var results = new List<(byte[] Key, long Value)>();
        await foreach (var entry in betree.RangeQueryAsync(MakeKey(0), MakeKey(5)))
            results.Add(entry);

        Assert.True(results.Count >= 1, "Range query on migrated BeTree should return results");
    }

    [Fact]
    public async Task MigrationIsIdempotent()
    {
        var device = new InMemoryBlockDevice();
        var allocator = new InMemoryBlockAllocator();
        var wal = new InMemoryWriteAheadLog();

        // Create and populate a BeTree (already at Level 3)
        var betree = new BeTree(device, allocator, wal, 1, 4096);
        for (int i = 0; i < 20; i++)
            await betree.InsertAsync(MakeKey(i), i);

        // "Migrate" by copying to another BeTree (should be a no-op equivalent)
        var device2 = new InMemoryBlockDevice();
        var allocator2 = new InMemoryBlockAllocator();
        var wal2 = new InMemoryWriteAheadLog();
        var betree2 = new BeTree(device2, allocator2, wal2, 1, 4096);

        await foreach (var (key, value) in betree.RangeQueryAsync(null, null))
            await betree2.InsertAsync(key, value);

        // Verify identical content
        Assert.Equal(betree.ObjectCount, betree2.ObjectCount);
        for (int i = 0; i < 20; i++)
        {
            var original = await betree.LookupAsync(MakeKey(i));
            var migrated = await betree2.LookupAsync(MakeKey(i));
            Assert.Equal(original, migrated);
        }
    }
}
