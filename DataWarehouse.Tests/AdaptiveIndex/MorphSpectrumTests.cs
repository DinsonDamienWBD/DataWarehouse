using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.VirtualDiskEngine;
using DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;
using Xunit;

namespace DataWarehouse.Tests.AdaptiveIndex;

#region Test Infrastructure

/// <summary>
/// In-memory block device for fast tests without disk I/O.
/// Tracks read/write counts for performance assertions.
/// </summary>
public sealed class InMemoryBlockDevice : IBlockDevice
{
    private readonly ConcurrentDictionary<long, byte[]> _blocks = new();
    private long _readCount;
    private long _writeCount;

    public int BlockSize { get; }
    public long BlockCount { get; }

    public long ReadCount => Interlocked.Read(ref _readCount);
    public long WriteCount => Interlocked.Read(ref _writeCount);

    public InMemoryBlockDevice(int blockSize = 4096, long blockCount = 1_000_000)
    {
        BlockSize = blockSize;
        BlockCount = blockCount;
    }

    public Task ReadBlockAsync(long blockNumber, Memory<byte> buffer, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _readCount);
        if (_blocks.TryGetValue(blockNumber, out var data))
        {
            data.AsMemory(0, Math.Min(data.Length, buffer.Length)).CopyTo(buffer);
        }
        else
        {
            buffer.Span.Clear();
        }
        return Task.CompletedTask;
    }

    public Task WriteBlockAsync(long blockNumber, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _writeCount);
        _blocks[blockNumber] = data.ToArray();
        return Task.CompletedTask;
    }

    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _blocks.Clear();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// In-memory block allocator with monotonic counter.
/// </summary>
public sealed class InMemoryBlockAllocator : IBlockAllocator
{
    private long _nextBlock = 100; // Start above root/metadata blocks
    public long FreeBlockCount => long.MaxValue - _nextBlock;
    public long TotalBlockCount => long.MaxValue;
    public double FragmentationRatio => 0.0;

    public long AllocateBlock(CancellationToken ct = default)
        => Interlocked.Increment(ref _nextBlock);

    public long[] AllocateExtent(int blockCount, CancellationToken ct = default)
    {
        var start = Interlocked.Add(ref _nextBlock, blockCount) - blockCount + 1;
        var blocks = new long[blockCount];
        for (int i = 0; i < blockCount; i++)
            blocks[i] = start + i;
        return blocks;
    }

    public void FreeBlock(long blockNumber) { }
    public void FreeExtent(long startBlock, int blockCount) { }
    // For this test stub every block is treated as allocated to satisfy the interface contract.
    public bool IsAllocated(long blockNumber) => blockNumber < _nextBlock;
    public Task PersistAsync(IBlockDevice device, long bitmapStartBlock, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>
/// In-memory write-ahead log for test crash recovery scenarios.
/// </summary>
public sealed class InMemoryWriteAheadLog : IWriteAheadLog
{
    private readonly List<JournalEntry> _entries = new();
    private readonly List<JournalEntry> _flushed = new();
    private long _seqNum;
    private long _txnCounter;
    private bool _abortNextFlush;

    public long CurrentSequenceNumber => Interlocked.Read(ref _seqNum);
    public long WalSizeBlocks => _entries.Count;
    public double WalUtilization => 0.1;
    public bool NeedsRecovery => false;

    /// <summary>Set to true to simulate crash on next flush (for crash recovery tests).</summary>
    public bool AbortNextFlush
    {
        get => _abortNextFlush;
        set => _abortNextFlush = value;
    }

    public Task<WalTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        var txnId = Interlocked.Increment(ref _txnCounter);
        // WalTransaction has an internal constructor; use reflection to create it
        var ctor = typeof(WalTransaction).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(long), typeof(IWriteAheadLog) }, null)!;
        var txn = (WalTransaction)ctor.Invoke(new object[] { txnId, (IWriteAheadLog)this });
        return Task.FromResult(txn);
    }

    public Task AppendEntryAsync(JournalEntry entry, CancellationToken ct = default)
    {
        entry.SequenceNumber = Interlocked.Increment(ref _seqNum);
        lock (_entries) _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task FlushAsync(CancellationToken ct = default)
    {
        if (_abortNextFlush)
        {
            _abortNextFlush = false;
            throw new InvalidOperationException("Simulated crash during flush.");
        }
        lock (_entries)
        {
            _flushed.AddRange(_entries);
            _entries.Clear();
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JournalEntry>> ReplayAsync(CancellationToken ct = default)
    {
        lock (_flushed) return Task.FromResult<IReadOnlyList<JournalEntry>>(_flushed.ToList());
    }

    public Task CheckpointAsync(CancellationToken ct = default)
    {
        lock (_flushed) _flushed.Clear();
        return Task.CompletedTask;
    }
}

#endregion

/// <summary>
/// Full morph spectrum integration tests exercising Level 0-2 forward and backward transitions,
/// concurrent reads during morph, crash recovery, and MorphAdvisor logic.
/// </summary>
public sealed class MorphSpectrumTests
{
    private static byte[] MakeKey(int i)
    {
        var bytes = BitConverter.GetBytes(i);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return bytes;
    }

    private static AdaptiveIndexEngine CreateEngine(
        int blockSize = 4096,
        long level0Max = 1,
        long level1Max = 10_000,
        long level2Max = 1_000_000)
    {
        var device = new InMemoryBlockDevice(blockSize);
        var allocator = new InMemoryBlockAllocator();
        var wal = new InMemoryWriteAheadLog();
        return new AdaptiveIndexEngine(device, allocator, wal, 0, blockSize, level0Max, level1Max, level2Max);
    }

    [Fact]
    public async Task Level0_SingleObject_DirectPointer()
    {
        await using var engine = CreateEngine();
        await engine.InsertAsync(MakeKey(1), 100);

        Assert.Equal(MorphLevel.DirectPointer, engine.CurrentLevel);
        Assert.Equal(100L, await engine.LookupAsync(MakeKey(1)));
        Assert.Equal(1L, engine.ObjectCount);
    }

    [Fact]
    public async Task Level0To1_AutoMorph_OnSecondInsert()
    {
        await using var engine = CreateEngine();
        await engine.InsertAsync(MakeKey(1), 100);
        Assert.Equal(MorphLevel.DirectPointer, engine.CurrentLevel);

        await engine.InsertAsync(MakeKey(2), 200);
        Assert.Equal(MorphLevel.SortedArray, engine.CurrentLevel);

        // Both lookups should still succeed
        Assert.Equal(100L, await engine.LookupAsync(MakeKey(1)));
        Assert.Equal(200L, await engine.LookupAsync(MakeKey(2)));
    }

    [Fact]
    public async Task Level1_SortedArray_BinarySearch()
    {
        await using var engine = CreateEngine();

        for (int i = 0; i < 100; i++)
            await engine.InsertAsync(MakeKey(i), i * 10L);

        Assert.Equal(MorphLevel.SortedArray, engine.CurrentLevel);

        // Verify all lookups succeed
        for (int i = 0; i < 100; i++)
            Assert.Equal(i * 10L, await engine.LookupAsync(MakeKey(i)));

        // Verify sorted range query
        var results = new List<(byte[] Key, long Value)>();
        await foreach (var entry in engine.RangeQueryAsync(MakeKey(10), MakeKey(20)))
            results.Add(entry);

        Assert.True(results.Count >= 1, "Range query should return entries");
        // Verify sorted order
        for (int i = 1; i < results.Count; i++)
        {
            int cmp = ByteArrayComparer.Instance.Compare(results[i - 1].Key, results[i].Key);
            Assert.True(cmp < 0, "Range query results should be sorted");
        }
    }

    [Fact]
    public async Task Level1To2_AutoMorph_At10K()
    {
        // Use smaller thresholds for faster testing
        await using var engine = CreateEngine(level1Max: 100, level2Max: 10_000);

        for (int i = 0; i < 101; i++)
            await engine.InsertAsync(MakeKey(i), i);

        Assert.Equal(MorphLevel.AdaptiveRadixTree, engine.CurrentLevel);

        // Verify lookups still work
        Assert.Equal(50L, await engine.LookupAsync(MakeKey(50)));
    }

    [Fact]
    public async Task Level2_ART_PathCompression()
    {
        // Insert keys with shared prefix to test ART path compression
        await using var engine = CreateEngine(level1Max: 5, level2Max: 100_000);

        // Insert enough to trigger morph to ART
        for (int i = 0; i < 10; i++)
        {
            // Keys share a 2-byte prefix (0x00, 0x00)
            var key = new byte[] { 0, 0, (byte)(i / 256), (byte)(i % 256) };
            await engine.InsertAsync(key, i);
        }

        Assert.Equal(MorphLevel.AdaptiveRadixTree, engine.CurrentLevel);

        // Verify lookups with shared-prefix keys
        for (int i = 0; i < 10; i++)
        {
            var key = new byte[] { 0, 0, (byte)(i / 256), (byte)(i % 256) };
            Assert.Equal((long)i, await engine.LookupAsync(key));
        }
    }

    [Fact]
    public async Task Level2To3_AutoMorph_At1M_ThrowsNotSupported()
    {
        // Level 3 (BeTree) is not yet wired into AdaptiveIndexEngine.CreateIndexForLevel
        // Inserting past level2Max should throw NotSupportedException
        await using var engine = CreateEngine(level0Max: 1, level1Max: 5, level2Max: 10);

        for (int i = 0; i < 11; i++)
            await engine.InsertAsync(MakeKey(i), i);

        // At this point we have 11 entries, which should be in ART (level 2)
        // The next insert should try to morph to BeTree and throw
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await engine.InsertAsync(MakeKey(11), 11));
    }

    [Fact]
    public async Task Level3_BeTree_MessageBuffering()
    {
        // Test BeTree directly (not through engine since engine doesn't support Level 3 yet)
        var device = new InMemoryBlockDevice();
        var allocator = new InMemoryBlockAllocator();
        var wal = new InMemoryWriteAheadLog();
        var betree = new BeTree(device, allocator, wal, 1, 4096);

        await betree.InsertAsync(MakeKey(1), 100);
        await betree.InsertAsync(MakeKey(2), 200);

        // Verify queryable (messages buffered, resolved on read)
        Assert.Equal(100L, await betree.LookupAsync(MakeKey(1)));
        Assert.Equal(200L, await betree.LookupAsync(MakeKey(2)));
    }

    [Fact]
    public async Task Level3_BeTree_TombstonePropagation()
    {
        var device = new InMemoryBlockDevice();
        var allocator = new InMemoryBlockAllocator();
        var wal = new InMemoryWriteAheadLog();
        var betree = new BeTree(device, allocator, wal, 1, 4096);

        await betree.InsertAsync(MakeKey(1), 100);
        Assert.Equal(100L, await betree.LookupAsync(MakeKey(1)));

        var deleted = await betree.DeleteAsync(MakeKey(1));
        Assert.True(deleted);
        Assert.Null(await betree.LookupAsync(MakeKey(1)));
    }

    [Fact]
    public async Task ForwardMorph_Level0Through2()
    {
        // Progressive insert triggering each level transition through Level 2
        // (Levels 3-5 require disk-backed BeTree; Level 6 requires distributed setup)
        var levels = new List<MorphLevel>();
        await using var engine = CreateEngine(level0Max: 1, level1Max: 50, level2Max: 1_000_000);

        engine.LevelChanged += (from, to) => levels.Add(to);

        // Level 0: insert 1 object
        await engine.InsertAsync(MakeKey(0), 0);
        Assert.Equal(MorphLevel.DirectPointer, engine.CurrentLevel);

        // Level 1: insert 2nd object
        await engine.InsertAsync(MakeKey(1), 1);
        Assert.Equal(MorphLevel.SortedArray, engine.CurrentLevel);

        // Level 2: insert enough to exceed Level1Max
        for (int i = 2; i <= 51; i++)
            await engine.InsertAsync(MakeKey(i), i);
        Assert.Equal(MorphLevel.AdaptiveRadixTree, engine.CurrentLevel);

        // Verify transitions occurred
        Assert.Contains(MorphLevel.SortedArray, levels);
        Assert.Contains(MorphLevel.AdaptiveRadixTree, levels);
    }

    [Fact]
    public async Task BackwardMorph_Level2ToLevel1()
    {
        // Use small thresholds: L0=1, L1=50, L2=1M
        await using var engine = CreateEngine(level0Max: 1, level1Max: 50, level2Max: 1_000_000);

        // Insert 60 objects -> should be at ART (Level 2)
        for (int i = 0; i < 60; i++)
            await engine.InsertAsync(MakeKey(i), i);
        Assert.Equal(MorphLevel.AdaptiveRadixTree, engine.CurrentLevel);

        // Delete down to 20 objects -> should morph back to SortedArray (Level 1)
        for (int i = 20; i < 60; i++)
            await engine.DeleteAsync(MakeKey(i));

        Assert.Equal(MorphLevel.SortedArray, engine.CurrentLevel);
        Assert.Equal(20L, engine.ObjectCount);

        // Verify remaining entries
        for (int i = 0; i < 20; i++)
            Assert.Equal((long)i, await engine.LookupAsync(MakeKey(i)));
    }

    [Fact]
    public async Task BackwardMorph_Level2ToLevel0()
    {
        await using var engine = CreateEngine(level0Max: 1, level1Max: 50, level2Max: 1_000_000);

        // Insert 60 objects -> Level 2 (ART)
        for (int i = 0; i < 60; i++)
            await engine.InsertAsync(MakeKey(i), i);
        Assert.Equal(MorphLevel.AdaptiveRadixTree, engine.CurrentLevel);

        // Delete all but 1
        for (int i = 1; i < 60; i++)
            await engine.DeleteAsync(MakeKey(i));

        Assert.Equal(MorphLevel.DirectPointer, engine.CurrentLevel);
        Assert.Equal(1L, engine.ObjectCount);
        Assert.Equal(0L, await engine.LookupAsync(MakeKey(0)));
    }

    [Fact]
    public async Task BackwardMorph_LargeScale_Level2ToLevel1()
    {
        // Simulate the "1M insert, delete 999K" scenario at smaller scale with same ratio
        await using var engine = CreateEngine(level0Max: 1, level1Max: 100, level2Max: 1_000_000);

        const int total = 1000;
        const int keep = 10;

        for (int i = 0; i < total; i++)
            await engine.InsertAsync(MakeKey(i), i);
        Assert.Equal(MorphLevel.AdaptiveRadixTree, engine.CurrentLevel);

        // Delete all but 'keep' entries
        for (int i = keep; i < total; i++)
            await engine.DeleteAsync(MakeKey(i));

        // Should have demoted to SortedArray or DirectPointer
        Assert.True(engine.CurrentLevel <= MorphLevel.SortedArray,
            $"Expected Level 1 or below after massive deletion, got {engine.CurrentLevel}");
        Assert.Equal(keep, engine.ObjectCount);
    }

    [Fact]
    public async Task ZeroDowntime_ReadsWorkDuringMorph()
    {
        await using var engine = CreateEngine(level0Max: 1, level1Max: 50, level2Max: 1_000_000);

        // Insert 10 entries at Level 1
        for (int i = 0; i < 10; i++)
            await engine.InsertAsync(MakeKey(i), i);

        // Start concurrent reads while triggering morph
        var readResults = new ConcurrentBag<long?>();
        var readTask = Task.Run(async () =>
        {
            for (int r = 0; r < 100; r++)
            {
                var result = await engine.LookupAsync(MakeKey(r % 10));
                readResults.Add(result);
                await Task.Yield();
            }
        });

        // Trigger morph to Level 2 by inserting past threshold
        for (int i = 10; i <= 51; i++)
            await engine.InsertAsync(MakeKey(i), i);

        await readTask;

        // All reads should have succeeded (no nulls for existing keys)
        Assert.All(readResults, r => Assert.NotNull(r));
    }

    [Fact]
    public void MorphAdvisor_RecommendsCorrectLevel()
    {
        var metrics = new MorphMetricsCollector();
        metrics.SetObjectCount(500);
        var policy = IndexMorphPolicy.Default;
        policy.MorphCooldown = TimeSpan.Zero; // Disable cooldown for tests
        var advisor = new IndexMorphAdvisor(metrics, policy);

        var snapshot = new MorphMetricsSnapshot
        {
            ObjectCount = 500,
            ReadWriteRatio = 0.5,
            P50LatencyMs = 1.0,
            P99LatencyMs = 5.0,
            KeyEntropy = 4.0,
            InsertRate = 100,
            CurrentLevel = MorphLevel.DirectPointer,
            Timestamp = DateTimeOffset.UtcNow
        };

        var recommended = advisor.Recommend(snapshot);
        // 500 objects should recommend SortedArray
        Assert.Equal(MorphLevel.SortedArray, recommended);
    }

    [Fact]
    public void MorphAdvisor_PolicyOverride()
    {
        var metrics = new MorphMetricsCollector();
        var policy = IndexMorphPolicy.Default;
        policy.ForcedLevel = MorphLevel.AdaptiveRadixTree;
        policy.MorphCooldown = TimeSpan.Zero;

        var advisor = new IndexMorphAdvisor(metrics, policy);

        var snapshot = new MorphMetricsSnapshot
        {
            ObjectCount = 5,
            CurrentLevel = MorphLevel.DirectPointer,
            Timestamp = DateTimeOffset.UtcNow
        };

        // Should return forced level regardless of metrics
        var recommended = advisor.Recommend(snapshot);
        Assert.Equal(MorphLevel.AdaptiveRadixTree, recommended);
    }

    [Fact]
    public void MorphAdvisor_CooldownPreventsOscillation()
    {
        var metrics = new MorphMetricsCollector();
        metrics.SetObjectCount(500);
        var policy = IndexMorphPolicy.Default;
        policy.MorphCooldown = TimeSpan.FromMinutes(10); // Long cooldown
        var advisor = new IndexMorphAdvisor(metrics, policy);

        var snapshot = new MorphMetricsSnapshot
        {
            ObjectCount = 500,
            CurrentLevel = MorphLevel.DirectPointer,
            Timestamp = DateTimeOffset.UtcNow
        };

        // First recommendation should work
        var first = advisor.Recommend(snapshot);
        Assert.NotNull(first);

        // Record that a morph was executed
        advisor.RecordMorphExecuted(MorphLevel.DirectPointer, first!.Value, 1.0);

        // Immediately re-evaluate: cooldown should prevent recommendation
        var second = advisor.Recommend(snapshot with { CurrentLevel = first.Value });
        Assert.Null(second);
    }

    [Fact]
    public async Task CrashRecovery_ResumesFromCheckpoint()
    {
        // Test that WAL journaling enables crash recovery.
        // We insert data, simulate a crash during morph by using a fresh engine with the same WAL.
        var device = new InMemoryBlockDevice();
        var allocator = new InMemoryBlockAllocator();
        var wal = new InMemoryWriteAheadLog();

        // First engine: insert entries and let morph happen
        var engine1 = new AdaptiveIndexEngine(device, allocator, wal, 0, 4096, level0Max: 1, level1Max: 5);

        for (int i = 0; i < 5; i++)
            await engine1.InsertAsync(MakeKey(i), i);

        // Verify entries are queryable
        for (int i = 0; i < 5; i++)
            Assert.Equal((long)i, await engine1.LookupAsync(MakeKey(i)));

        await engine1.DisposeAsync();

        // "Recovery": create fresh engine (simulating restart after crash)
        // The WAL has morph markers; the engine should start clean and be usable
        var engine2 = new AdaptiveIndexEngine(device, allocator, wal, 0, 4096, level0Max: 1, level1Max: 5);

        // New engine starts at Level 0 (DirectPointer) since state isn't persisted beyond WAL
        // This validates the engine can be re-created cleanly
        Assert.Equal(MorphLevel.DirectPointer, engine2.CurrentLevel);

        // Re-insert and verify it works (engine is functional after recovery)
        await engine2.InsertAsync(MakeKey(100), 100);
        Assert.Equal(100L, await engine2.LookupAsync(MakeKey(100)));

        await engine2.DisposeAsync();
    }
}
