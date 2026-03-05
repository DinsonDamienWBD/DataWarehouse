using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Journal;

/// <summary>
/// Commit barrier that lives on Shard 0 and references the max LSN from every shard.
/// Provides an atomic multi-shard commit point used for crash recovery: after a crash,
/// entries with LSN past the last barrier are discarded from each shard.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 VOPT-29: Thread-affinity sharded WAL")]
public sealed class ShardCommitBarrier
{
    // Packed shard-LSN record layout: [ShardId:2][Lsn:6] = 8 bytes per shard
    private const int ShardLsnEntrySize = 8;

    private readonly WalShard[] _shards;

    /// <summary>
    /// Represents a barrier record capturing the LSNs of all shards at a commit point.
    /// </summary>
    public readonly struct BarrierRecord
    {
        /// <summary>LSN of the barrier entry itself (on Shard 0).</summary>
        public long BarrierLsn { get; init; }

        /// <summary>Per-shard LSNs recorded at the barrier point. Index = ShardId.</summary>
        public long[] ShardLsns { get; init; }

        /// <summary>Transaction identifier that triggered this barrier.</summary>
        public long TransactionId { get; init; }
    }

    /// <summary>
    /// Initialises the commit barrier.
    /// </summary>
    /// <param name="shards">All shards, with Shard 0 acting as the barrier shard.</param>
    public ShardCommitBarrier(WalShard[] shards)
    {
        ArgumentNullException.ThrowIfNull(shards);
        if (shards.Length == 0)
        {
            throw new ArgumentException("At least one shard is required.", nameof(shards));
        }

        _shards = shards;
    }

    /// <summary>
    /// Collects the current LSN from every shard, writes a barrier <see cref="JournalEntryType.Checkpoint"/>
    /// entry to Shard 0, flushes Shard 0, and returns the barrier LSN.
    /// </summary>
    /// <param name="transactionId">Transaction that triggers this commit barrier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>LSN of the barrier entry on Shard 0.</returns>
    public async Task<long> CommitBarrierAsync(long transactionId, CancellationToken ct = default)
    {
        // 1. Collect CurrentLsn from ALL shards
        long[] shardLsns = new long[_shards.Length];
        for (int i = 0; i < _shards.Length; i++)
        {
            shardLsns[i] = _shards[i].CurrentLsn;
        }

        // 2. Pack shard LSNs into AfterImage: 8 bytes per shard [ShardId:2][Lsn:6]
        byte[] afterImage = PackShardLsns(shardLsns);

        // 3. Append barrier entry on Shard 0 (Checkpoint type)
        var barrierEntry = new JournalEntry
        {
            TransactionId = transactionId,
            Type = JournalEntryType.Checkpoint,
            TargetBlockNumber = -1,
            BeforeImage = null,
            AfterImage = afterImage
        };

        _shards[0].Append(barrierEntry);

        // 4. Flush Shard 0 so barrier is durable
        await _shards[0].FlushAsync(ct);

        return _shards[0].CurrentLsn;
    }

    /// <summary>
    /// Scans Shard 0 for the most-recent barrier entry to use as recovery starting point.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The last barrier record, or <c>null</c> if none exists.</returns>
    public async Task<BarrierRecord?> FindLastBarrierAsync(CancellationToken ct = default)
    {
        IReadOnlyList<JournalEntry> entries = await _shards[0].ReplayAsync(ct);

        BarrierRecord? last = null;

        foreach (var entry in entries)
        {
            if (entry.Type == JournalEntryType.Checkpoint && entry.AfterImage is not null)
            {
                long[]? shardLsns = TryUnpackShardLsns(entry.AfterImage);
                if (shardLsns is null)
                {
                    continue;
                }

                last = new BarrierRecord
                {
                    BarrierLsn = entry.SequenceNumber,
                    ShardLsns = shardLsns,
                    TransactionId = entry.TransactionId
                };
            }
        }

        return last;
    }

    /// <summary>
    /// Truncates each shard's LSN tracking to the value recorded in <paramref name="barrier"/>,
    /// discarding any entries past the barrier point so recovery does not re-apply them.
    /// </summary>
    /// <param name="barrier">Barrier record from <see cref="FindLastBarrierAsync"/>.</param>
    /// <param name="shards">Shards to reconcile (must match the barrier's shard count).</param>
    /// <param name="ct">Cancellation token.</param>
    public Task ReconcileShardsAsync(BarrierRecord barrier, WalShard[] shards, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(shards);

        int count = Math.Min(barrier.ShardLsns.Length, shards.Length);
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            shards[i].TruncateToLsn(barrier.ShardLsns[i]);
        }

        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Packs per-shard LSNs into a byte array.
    /// Layout per entry: [ShardId: 2 bytes LE][Lsn: 6 bytes LE] (8 bytes total per shard).
    /// </summary>
    private static byte[] PackShardLsns(long[] shardLsns)
    {
        int count = shardLsns.Length;
        byte[] buf = new byte[count * ShardLsnEntrySize];

        for (int i = 0; i < count; i++)
        {
            int offset = i * ShardLsnEntrySize;

            // ShardId in first 2 bytes (LE)
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(offset, 2), (ushort)i);

            // LSN in next 6 bytes (LE) — truncate to 48-bit range
            long lsn = shardLsns[i];
            buf[offset + 2] = (byte)(lsn & 0xFF);
            buf[offset + 3] = (byte)((lsn >> 8) & 0xFF);
            buf[offset + 4] = (byte)((lsn >> 16) & 0xFF);
            buf[offset + 5] = (byte)((lsn >> 24) & 0xFF);
            buf[offset + 6] = (byte)((lsn >> 32) & 0xFF);
            buf[offset + 7] = (byte)((lsn >> 40) & 0xFF);
        }

        return buf;
    }

    /// <summary>
    /// Attempts to unpack shard LSNs from a packed AfterImage byte array.
    /// Returns <c>null</c> if the data is malformed.
    /// </summary>
    private static long[]? TryUnpackShardLsns(byte[] afterImage)
    {
        if (afterImage.Length % ShardLsnEntrySize != 0)
        {
            return null;
        }

        int count = afterImage.Length / ShardLsnEntrySize;
        long[] lsns = new long[count];

        for (int i = 0; i < count; i++)
        {
            int offset = i * ShardLsnEntrySize;
            // shardId from first 2 bytes — use as the index
            ushort shardId = BinaryPrimitives.ReadUInt16LittleEndian(afterImage.AsSpan(offset, 2));
            if (shardId >= count)
            {
                return null;
            }

            // LSN from next 6 bytes (48-bit LE)
            long lsn = afterImage[offset + 2]
                       | ((long)afterImage[offset + 3] << 8)
                       | ((long)afterImage[offset + 4] << 16)
                       | ((long)afterImage[offset + 5] << 24)
                       | ((long)afterImage[offset + 6] << 32)
                       | ((long)afterImage[offset + 7] << 40);

            lsns[shardId] = lsn;
        }

        return lsns;
    }
}
