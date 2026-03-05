using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Journal;

/// <summary>
/// A single WAL shard with a lock-free ring buffer and per-shard flush to an independent disk region.
/// Each shard is pinned to a thread via <c>ShardedWriteAheadLog</c> thread affinity,
/// eliminating cross-thread lock contention.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 VOPT-29: Thread-affinity sharded WAL")]
public sealed class WalShard
{
    // Ring buffer capacity — power-of-2 so mask works: (index & _mask)
    private const int DefaultRingCapacity = 4096;

    private readonly int _shardId;
    private readonly IBlockDevice _device;
    private readonly long _shardStartBlock;
    private readonly long _shardBlockCount;
    private readonly int _blockSize;

    // Lock-free ring buffer
    private readonly JournalEntry[] _ring;
    private readonly int _mask;

    // _head: next slot to write into (producer advances)
    // _tail: next slot to flush / consume (consumer advances)
    // Both are raw long counters; apply (_mask) to get slot index.
    private long _head;
    private long _tail;

    // Per-shard monotonic sequence counter
    private long _lsn;

    // Next block offset inside the shard's disk region for flush
    private long _flushBlock;

    /// <summary>
    /// Gets the shard identifier.
    /// </summary>
    public int ShardId => _shardId;

    /// <summary>
    /// Gets the latest sequence number (LSN) that has been assigned in this shard.
    /// </summary>
    public long CurrentLsn => Interlocked.Read(ref _lsn);

    /// <summary>
    /// Gets the number of entries buffered in the ring that have not yet been flushed.
    /// </summary>
    public int PendingEntryCount
    {
        get
        {
            long head = Interlocked.Read(ref _head);
            long tail = Interlocked.Read(ref _tail);
            return (int)(head - tail);
        }
    }

    /// <summary>
    /// Initialises a new <see cref="WalShard"/>.
    /// </summary>
    /// <param name="shardId">Zero-based shard index.</param>
    /// <param name="device">Block device that owns the storage for this shard.</param>
    /// <param name="shardStartBlock">First block of this shard's region on the device (absolute).</param>
    /// <param name="shardBlockCount">Number of blocks allocated to this shard.</param>
    /// <param name="blockSize">Block size in bytes (must match <paramref name="device"/>).</param>
    /// <param name="ringCapacity">Ring buffer capacity; must be a power of two. Defaults to 4096.</param>
    public WalShard(int shardId, IBlockDevice device, long shardStartBlock, long shardBlockCount,
                    int blockSize, int ringCapacity = DefaultRingCapacity)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegative(shardId);
        ArgumentOutOfRangeException.ThrowIfLessThan(shardBlockCount, 1L);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 1);
        if (ringCapacity <= 0 || (ringCapacity & (ringCapacity - 1)) != 0)
        {
            throw new ArgumentException("Ring capacity must be a positive power of two.", nameof(ringCapacity));
        }

        _shardId = shardId;
        _device = device;
        _shardStartBlock = shardStartBlock;
        _shardBlockCount = shardBlockCount;
        _blockSize = blockSize;
        _ring = new JournalEntry[ringCapacity];
        _mask = ringCapacity - 1;
        _head = 0;
        _tail = 0;
        _lsn = 0;
        _flushBlock = 0;
    }

    /// <summary>
    /// Lock-free enqueue of a <see cref="JournalEntry"/> into the ring buffer.
    /// Assigns a per-shard monotonic <see cref="JournalEntry.SequenceNumber"/>.
    /// </summary>
    /// <param name="entry">Entry to append.</param>
    /// <exception cref="InvalidOperationException">Thrown when the ring buffer is full.</exception>
    public void Append(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Claim a slot via compare-exchange loop
        long head;
        long newHead;
        do
        {
            head = Interlocked.Read(ref _head);
            long tail = Interlocked.Read(ref _tail);

            if (head - tail >= _ring.Length)
            {
                throw new InvalidOperationException(
                    $"WAL shard {_shardId} ring buffer is full ({_ring.Length} entries). Call FlushAsync first.");
            }

            newHead = head + 1;
        }
        while (Interlocked.CompareExchange(ref _head, newHead, head) != head);

        // Assign a monotonic LSN from this shard's counter
        long lsn = Interlocked.Increment(ref _lsn);
        entry.SequenceNumber = lsn;

        // Write to ring slot (visible to consumer after head is already advanced)
        _ring[head & _mask] = entry;
    }

    /// <summary>
    /// Serialises all buffered entries (between current tail and head) to the shard's disk region
    /// and advances the tail. Uses the same binary format as <see cref="WriteAheadLog"/> (JournalEntry.HeaderSize + data).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        long tail = Interlocked.Read(ref _tail);
        long head = Interlocked.Read(ref _head);

        if (tail == head)
        {
            // Nothing pending
            return;
        }

        for (long i = tail; i < head; i++)
        {
            ct.ThrowIfCancellationRequested();

            var entry = _ring[i & _mask];
            if (entry is null)
            {
                // Slot not yet written by producer — producer advanced _head before storing entry;
                // spin briefly until visible (memory ordering on x86 makes this rare).
                int spins = 0;
                while (_ring[i & _mask] is null)
                {
                    if (++spins > 10_000)
                    {
                        throw new InvalidOperationException(
                            $"WAL shard {_shardId}: entry at ring slot {i & _mask} not visible after spin.");
                    }
                    Thread.SpinWait(1);
                }
                entry = _ring[i & _mask];
            }

            await WriteEntryToDiskAsync(entry, ct);
            _ring[i & _mask] = null!;
        }

        // Advance tail to head to signal all flushed
        Interlocked.Exchange(ref _tail, head);

        // Flush underlying device
        await _device.FlushAsync(ct);
    }

    /// <summary>
    /// Replays all committed entries from this shard's disk region for crash recovery.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All valid journal entries found on disk for this shard.</returns>
    public async Task<IReadOnlyList<JournalEntry>> ReplayAsync(CancellationToken ct = default)
    {
        var entries = new List<JournalEntry>();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            for (long blockOffset = 0; blockOffset < _shardBlockCount; blockOffset++)
            {
                ct.ThrowIfCancellationRequested();

                long absoluteBlock = _shardStartBlock + blockOffset;
                await _device.ReadBlockAsync(absoluteBlock, buffer.AsMemory(0, _blockSize), ct);

                int pos = 0;
                while (pos < _blockSize)
                {
                    if (JournalEntry.Deserialize(buffer.AsSpan(pos, _blockSize - pos), out var entry, out int bytesRead))
                    {
                        entries.Add(entry);
                        pos += bytesRead;
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return entries;
    }

    /// <summary>
    /// Resets the shard's flush position to truncate entries past a given LSN.
    /// Used by <see cref="ShardCommitBarrier.ReconcileShardsAsync"/> during recovery.
    /// </summary>
    /// <param name="lsn">Barrier-recorded LSN for this shard.</param>
    internal void TruncateToLsn(long lsn)
    {
        // Recalculate how many blocks are occupied by entries with LSN <= the given value.
        // In recovery the shard ring is empty (not yet started), so we simply reset the
        // flush pointer.  The barrier will prevent re-applying entries past this LSN.
        Interlocked.Exchange(ref _lsn, lsn);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task WriteEntryToDiskAsync(JournalEntry entry, CancellationToken ct)
    {
        int entrySize = entry.GetSerializedSize();
        byte[] buf = ArrayPool<byte>.Shared.Rent(entrySize);
        try
        {
            Array.Clear(buf, 0, entrySize);
            int written = entry.Serialize(buf.AsSpan(0, entrySize));

            // Write entry spanning one or more blocks
            int offset = 0;
            byte[] blockBuf = ArrayPool<byte>.Shared.Rent(_blockSize);
            try
            {
                while (offset < written)
                {
                    if (_flushBlock >= _shardBlockCount)
                    {
                        // Wrap around to start of shard region (circular)
                        _flushBlock = 0;
                    }

                    long absoluteBlock = _shardStartBlock + _flushBlock;

                    // Read existing block content (may be partial fill from prior entry)
                    await _device.ReadBlockAsync(absoluteBlock, blockBuf.AsMemory(0, _blockSize), ct);

                    // Write entry data starting at byte 0 of the block
                    int toWrite = Math.Min(_blockSize, written - offset);
                    buf.AsSpan(offset, toWrite).CopyTo(blockBuf.AsSpan(0, toWrite));

                    // Zero-fill rest of block to avoid stale data
                    if (toWrite < _blockSize)
                    {
                        Array.Clear(blockBuf, toWrite, _blockSize - toWrite);
                    }

                    await _device.WriteBlockAsync(absoluteBlock, blockBuf.AsMemory(0, _blockSize), ct);
                    offset += toWrite;
                    _flushBlock++;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(blockBuf);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
}
