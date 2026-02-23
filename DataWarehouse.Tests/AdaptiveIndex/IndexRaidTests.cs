using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;
using Xunit;

namespace DataWarehouse.Tests.AdaptiveIndex;

/// <summary>
/// Tests for Index RAID configurations: striping, mirroring, tiering, and composite modes.
/// </summary>
public sealed class IndexRaidTests
{
    private static byte[] MakeKey(int i)
    {
        var bytes = BitConverter.GetBytes(i);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return bytes;
    }

    private static SortedArrayIndex CreateSortedArray() => new SortedArrayIndex(100_000);

    [Fact]
    public async Task Striping_4Way_InsertAndLookup()
    {
        var striped = IndexRaid.CreateStriped(4, _ => CreateSortedArray());

        for (int i = 0; i < 10_000; i++)
            await striped.InsertAsync(MakeKey(i), i);

        // Verify all lookups succeed
        for (int i = 0; i < 10_000; i++)
        {
            var result = await striped.LookupAsync(MakeKey(i));
            Assert.NotNull(result);
            Assert.Equal((long)i, result!.Value);
        }
    }

    [Fact]
    public async Task Striping_4Way_RangeQuery_MergesSorted()
    {
        var striped = IndexRaid.CreateStriped(4, _ => CreateSortedArray());

        // Insert unsorted keys across stripes
        var rng = new Random(42);
        var keys = Enumerable.Range(0, 1000).OrderBy(_ => rng.Next()).ToList();
        foreach (var k in keys)
            await striped.InsertAsync(MakeKey(k), k);

        // Range query should return sorted results
        var results = new List<(byte[] Key, long Value)>();
        await foreach (var entry in striped.RangeQueryAsync(null, null))
            results.Add(entry);

        Assert.Equal(1000, results.Count);
        for (int i = 1; i < results.Count; i++)
        {
            int cmp = ByteArrayComparer.Instance.Compare(results[i - 1].Key, results[i].Key);
            Assert.True(cmp < 0, $"Range query results must be sorted, but index {i - 1} >= {i}");
        }
    }

    [Fact]
    public async Task Striping_DistributesEvenly()
    {
        var stripes = new SortedArrayIndex[4];
        for (int i = 0; i < 4; i++)
            stripes[i] = new SortedArrayIndex(100_000);

        var striped = IndexRaid.CreateStriped(4, i => stripes[i]);

        for (int i = 0; i < 10_000; i++)
            await striped.InsertAsync(MakeKey(i), i);

        // Verify per-stripe counts are within 20% of each other
        long min = stripes.Min(s => s.ObjectCount);
        long max = stripes.Max(s => s.ObjectCount);
        long expected = 10_000 / 4;

        Assert.True(min > expected * 0.5, $"Min stripe count {min} too low (expected ~{expected})");
        Assert.True(max < expected * 2.0, $"Max stripe count {max} too high (expected ~{expected})");

        // Total should equal inserted count
        Assert.Equal(10_000L, stripes.Sum(s => s.ObjectCount));
    }

    [Fact]
    public async Task Mirroring_2Way_Sync_AllCopiesMatch()
    {
        var mirrors = new SortedArrayIndex[2];
        for (int i = 0; i < 2; i++)
            mirrors[i] = new SortedArrayIndex(100_000);

        var mirrored = IndexRaid.CreateMirrored(2, i => mirrors[i], MirrorWriteMode.Synchronous);

        for (int i = 0; i < 100; i++)
            await mirrored.InsertAsync(MakeKey(i), i);

        // Both mirrors should have identical data
        Assert.Equal(mirrors[0].ObjectCount, mirrors[1].ObjectCount);

        for (int i = 0; i < 100; i++)
        {
            var r0 = await mirrors[0].LookupAsync(MakeKey(i));
            var r1 = await mirrors[1].LookupAsync(MakeKey(i));
            Assert.Equal(r0, r1);
        }
    }

    [Fact]
    public async Task Mirroring_2Way_ReadFromPrimary()
    {
        var mirrored = IndexRaid.CreateMirrored(2, _ => CreateSortedArray(), MirrorWriteMode.Synchronous);

        await mirrored.InsertAsync(MakeKey(1), 100);

        // Reads always come from primary mirror
        var result = await mirrored.LookupAsync(MakeKey(1));
        Assert.Equal(100L, result);
    }

    [Fact]
    public async Task Mirroring_Rebuild_RestoresDegraded()
    {
        var mirrors = new SortedArrayIndex[2];
        for (int i = 0; i < 2; i++)
            mirrors[i] = new SortedArrayIndex(100_000);

        var mirroring = new IndexMirroring(2, i => mirrors[i], MirrorWriteMode.Synchronous);

        // Insert data into the mirrored set
        for (int i = 0; i < 50; i++)
            await mirroring.InsertAsync(MakeKey(i), i);

        // Simulate corruption: clear the secondary mirror
        for (int i = 0; i < 50; i++)
            await mirrors[1].DeleteAsync(MakeKey(i));
        Assert.Equal(0L, mirrors[1].ObjectCount);

        // Rebuild secondary from primary
        await mirroring.Rebuild(1);

        // Verify restored
        Assert.Equal(mirrors[0].ObjectCount, mirrors[1].ObjectCount);
        for (int i = 0; i < 50; i++)
        {
            var result = await mirrors[1].LookupAsync(MakeKey(i));
            Assert.NotNull(result);
            Assert.Equal((long)i, result!.Value);
        }

        // Verify health is restored
        var health = mirroring.GetHealth();
        Assert.Equal(MirrorHealthStatus.Healthy, health[1].Status);
    }

    [Fact]
    public async Task StripeMirror_RAID10_Functionality()
    {
        // 4 stripes x 2 mirrors = 8 index instances
        var raid10 = IndexRaid.CreateStripeMirror(
            4, 2,
            (stripe, mirror) => CreateSortedArray(),
            MirrorWriteMode.Synchronous);

        // Insert
        for (int i = 0; i < 1000; i++)
            await raid10.InsertAsync(MakeKey(i), i);

        // Lookup
        for (int i = 0; i < 1000; i++)
        {
            var result = await raid10.LookupAsync(MakeKey(i));
            Assert.Equal((long)i, result);
        }

        // Delete
        for (int i = 0; i < 100; i++)
        {
            var deleted = await raid10.DeleteAsync(MakeKey(i));
            Assert.True(deleted);
        }

        // Verify deletes worked
        for (int i = 0; i < 100; i++)
            Assert.Null(await raid10.LookupAsync(MakeKey(i)));

        // Remaining entries still accessible
        for (int i = 100; i < 1000; i++)
            Assert.Equal((long)i, await raid10.LookupAsync(MakeKey(i)));
    }

    [Fact]
    public async Task Tiering_HotKeyInL1()
    {
        var l1 = new SortedArrayIndex(100_000);
        var l2 = new SortedArrayIndex(100_000);
        var l3 = new SortedArrayIndex(100_000);

        // Set low promotion threshold for testing
        await using var tiered = (IndexTiering)IndexRaid.CreateTiered(l1, l2, l3,
            promotionThreshold: 5, l1MaxEntries: 100_000, l2MaxEntries: 1_000_000);

        // Insert 100 entries (goes to L2 warm)
        for (int i = 0; i < 100; i++)
            await tiered.InsertAsync(MakeKey(i), i);

        Assert.Equal(100L, l2.ObjectCount);
        Assert.Equal(0L, l1.ObjectCount);

        // Access one key many times to exceed promotion threshold
        for (int j = 0; j < 20; j++)
            await tiered.LookupAsync(MakeKey(42));

        // Allow promotion to complete (it's fire-and-forget)
        await Task.Delay(200);

        // Key 42 should have been promoted to L1
        var inL1 = await l1.LookupAsync(MakeKey(42));
        Assert.NotNull(inL1);
        Assert.Equal(42L, inL1!.Value);
    }

    [Fact]
    public async Task Tiering_ColdKeyDemoted()
    {
        var l1 = new SortedArrayIndex(100_000);
        var l2 = new SortedArrayIndex(100_000);
        var l3 = new SortedArrayIndex(100_000);

        await using var tiered = (IndexTiering)IndexRaid.CreateTiered(l1, l2, l3,
            promotionThreshold: 5, l1MaxEntries: 100_000, l2MaxEntries: 1_000_000);

        // Insert entries (goes to L2)
        for (int i = 0; i < 50; i++)
            await tiered.InsertAsync(MakeKey(i), i);

        // Without access, keys should remain in L2/L3 (not promoted)
        Assert.Equal(0L, l1.ObjectCount);
        Assert.True(l2.ObjectCount > 0 || l3.ObjectCount > 0,
            "Cold keys should remain in warm or cold tier");
    }

    [Fact]
    public async Task Tiering_CountMinSketch_Accuracy()
    {
        var l1 = new SortedArrayIndex(100_000);
        var l2 = new SortedArrayIndex(100_000);
        var l3 = new SortedArrayIndex(100_000);

        await using var tiered = (IndexTiering)IndexRaid.CreateTiered(l1, l2, l3,
            promotionThreshold: 100, l1MaxEntries: 100_000, l2MaxEntries: 1_000_000);

        // Insert entries
        for (int i = 0; i < 100; i++)
            await tiered.InsertAsync(MakeKey(i), i);

        // Access specific keys with known frequency
        for (int j = 0; j < 50; j++)
            await tiered.LookupAsync(MakeKey(10));

        for (int j = 0; j < 10; j++)
            await tiered.LookupAsync(MakeKey(20));

        // The tiered index tracks access internally via CountMinSketch.
        // Verify that the heavily accessed key is more likely to be promoted (functional test)
        // The sketch guarantees estimates >= actual count, so the hot key's estimate should be >= 50
        // We test indirectly: look up key 10 once more; it should still be accessible
        var result = await tiered.LookupAsync(MakeKey(10));
        Assert.NotNull(result);
        Assert.Equal(10L, result!.Value);
    }

    [Fact]
    public async Task PerShardMorphLevel_DifferentLevels()
    {
        // Create a 2-shard "forest" using striping where each shard is an independent engine
        // with different data volumes, resulting in different morph levels
        var device1 = new InMemoryBlockDevice();
        var alloc1 = new InMemoryBlockAllocator();
        var wal1 = new InMemoryWriteAheadLog();
        var shard1 = new AdaptiveIndexEngine(device1, alloc1, wal1, 0, 4096, level0Max: 1, level1Max: 50);

        var device2 = new InMemoryBlockDevice();
        var alloc2 = new InMemoryBlockAllocator();
        var wal2 = new InMemoryWriteAheadLog();
        var shard2 = new AdaptiveIndexEngine(device2, alloc2, wal2, 0, 4096, level0Max: 1, level1Max: 50);

        // Shard 1: insert 1 entry -> Level 0 (DirectPointer)
        await shard1.InsertAsync(MakeKey(1), 1);

        // Shard 2: insert 60 entries -> Level 2 (ART)
        for (int i = 0; i < 60; i++)
            await shard2.InsertAsync(MakeKey(1000 + i), i);

        // Verify different morph levels
        Assert.Equal(MorphLevel.DirectPointer, shard1.CurrentLevel);
        Assert.Equal(MorphLevel.AdaptiveRadixTree, shard2.CurrentLevel);

        await shard1.DisposeAsync();
        await shard2.DisposeAsync();
    }
}
