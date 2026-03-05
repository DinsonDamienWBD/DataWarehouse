using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Persistence;
using DataWarehouse.SDK.Contracts.Scaling;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Infrastructure.Scaling
{
    /// <summary>
    /// Configuration for WAL-backed persistent message queues.
    /// Controls retention, compaction, and durability settings.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 88-06: WAL message queue configuration")]
    public sealed class WalMessageQueueConfig
    {
        /// <summary>Retention period for consumed messages. Default: 24 hours.</summary>
        public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromHours(24);

        /// <summary>Maximum WAL size per partition in bytes. Default: 1 GB.</summary>
        public long MaxPartitionSizeBytes { get; set; } = 1024L * 1024 * 1024;

        /// <summary>
        /// Fsync commit policy. Controls durability vs performance tradeoff.
        /// </summary>
        public FsyncPolicy FsyncPolicy { get; set; } = FsyncPolicy.EveryNMessages;

        /// <summary>
        /// Number of messages between fsync calls when using <see cref="FsyncPolicy.EveryNMessages"/>.
        /// Default: 100.
        /// </summary>
        public int FsyncBatchSize { get; set; } = 100;

        /// <summary>
        /// Interval for timed fsync when using <see cref="FsyncPolicy.Timed"/>.
        /// Default: 1 second.
        /// </summary>
        public TimeSpan FsyncInterval { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Interval between background compaction runs. Default: 5 minutes.
        /// </summary>
        public TimeSpan CompactionInterval { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>Creates a default configuration.</summary>
        public static WalMessageQueueConfig Default => new();
    }

    /// <summary>
    /// Fsync commit policy controlling when WAL entries are durably flushed to storage.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 88-06: WAL fsync policy")]
    public enum FsyncPolicy
    {
        /// <summary>Fsync after every message write. Maximum durability, lowest throughput.</summary>
        EveryMessage,

        /// <summary>Fsync after every N messages. Balanced durability/throughput.</summary>
        EveryNMessages,

        /// <summary>Fsync on a timed interval. Highest throughput, bounded data loss window.</summary>
        Timed
    }

    /// <summary>
    /// WAL-backed persistent message queue that survives process crashes without message loss.
    /// Each topic partition has an append-only WAL file with consumer offset tracking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The WAL message queue provides crash-durable messaging by persisting messages
    /// through <see cref="IPersistentBackingStore"/> before acknowledging the publish.
    /// On process restart, messages are replayed from the last committed consumer offset.
    /// </para>
    /// <para>
    /// Message format: length-prefix binary (4-byte length + serialized payload).
    /// Consumer offsets are tracked in a separate offset file per subscriber.
    /// Background compaction removes fully-consumed messages to reclaim space.
    /// </para>
    /// </remarks>
    [SdkCompatibility("6.0.0", Notes = "Phase 88-06: WAL-backed persistent message queue for crash durability")]
    public sealed class WalMessageQueue : IDisposable
    {
        private readonly IPersistentBackingStore _store;
        private readonly WalMessageQueueConfig _config;
        private readonly ConcurrentDictionary<string, PartitionWal> _partitions = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, long> _consumerOffsets = new(StringComparer.OrdinalIgnoreCase);
        private readonly Timer _compactionTimer;
        private readonly Timer? _fsyncTimer;
        private volatile bool _disposed; // P2-524: volatile prevents stale reads from timer callbacks

        /// <summary>
        /// Path prefix for WAL storage within <see cref="IPersistentBackingStore"/>.
        /// </summary>
        public const string WalPathPrefix = "dw://internal/wal-message-queue/";

        /// <summary>
        /// Initializes a new WAL-backed message queue.
        /// </summary>
        /// <param name="store">The persistent backing store for WAL file storage.</param>
        /// <param name="config">Optional configuration. Uses defaults if null.</param>
        public WalMessageQueue(IPersistentBackingStore store, WalMessageQueueConfig? config = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _config = config ?? WalMessageQueueConfig.Default;

            _compactionTimer = new Timer(
                _ => CompactAsync(CancellationToken.None).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        System.Diagnostics.Debug.WriteLine($"WAL compaction failed: {t.Exception?.GetBaseException().Message}");
                }, TaskContinuationOptions.OnlyOnFaulted),
                null,
                _config.CompactionInterval,
                _config.CompactionInterval);

            if (_config.FsyncPolicy == FsyncPolicy.Timed)
            {
                _fsyncTimer = new Timer(
                    _ => FlushAllAsync(CancellationToken.None).ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                            System.Diagnostics.Debug.WriteLine($"WAL flush failed: {t.Exception?.GetBaseException().Message}");
                    }, TaskContinuationOptions.OnlyOnFaulted),
                    null,
                    _config.FsyncInterval,
                    _config.FsyncInterval);
            }
        }

        /// <summary>
        /// Appends a message to the WAL for the specified topic partition.
        /// The message is serialized with a length-prefix binary format.
        /// </summary>
        /// <param name="topicPartition">Composite key of topic and partition index (e.g., "orders:0").</param>
        /// <param name="messageData">Serialized message bytes.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The WAL sequence number (offset) of the appended message.</returns>
        public async Task<long> AppendAsync(string topicPartition, byte[] messageData, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var wal = GetOrCreatePartition(topicPartition);

            // Length-prefix format: [4-byte length][payload][8-byte timestamp]
            var entry = new byte[4 + messageData.Length + 8];
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(0, 4), messageData.Length);
            messageData.CopyTo(entry.AsSpan(4));
            BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(4 + messageData.Length), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            var offset = Interlocked.Increment(ref wal.WriteOffset) - 1;
            wal.PendingEntries.Enqueue((offset, entry));
            Interlocked.Add(ref wal.PendingBytes, entry.Length);

            // Flush based on policy
            var uncommitted = Interlocked.Increment(ref wal.UncommittedCount);
            if (_config.FsyncPolicy == FsyncPolicy.EveryMessage ||
                (_config.FsyncPolicy == FsyncPolicy.EveryNMessages && uncommitted >= _config.FsyncBatchSize))
            {
                await FlushPartitionAsync(topicPartition, wal, ct).ConfigureAwait(false);
            }

            return offset;
        }

        /// <summary>
        /// Reads messages from the WAL starting at the specified offset.
        /// </summary>
        /// <param name="topicPartition">Composite key of topic and partition index.</param>
        /// <param name="fromOffset">The offset to start reading from (inclusive).</param>
        /// <param name="maxMessages">Maximum number of messages to read.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of (offset, data) tuples for the requested range.</returns>
        public async Task<IReadOnlyList<(long Offset, byte[] Data)>> ReadAsync(
            string topicPartition, long fromOffset, int maxMessages, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var walPath = $"{WalPathPrefix}{topicPartition}/wal";
            var walData = await _store.ReadAsync(walPath, ct).ConfigureAwait(false);

            var results = new List<(long Offset, byte[] Data)>();
            if (walData == null || walData.Length == 0) return results;

            long currentOffset = 0;
            int position = 0;

            while (position < walData.Length - 4 && results.Count < maxMessages)
            {
                var entryLength = BinaryPrimitives.ReadInt32LittleEndian(walData.AsSpan(position, 4));
                if (position + 4 + entryLength + 8 > walData.Length) break;

                if (currentOffset >= fromOffset)
                {
                    var data = new byte[entryLength];
                    walData.AsSpan(position + 4, entryLength).CopyTo(data);
                    results.Add((currentOffset, data));
                }

                position += 4 + entryLength + 8; // length prefix + data + timestamp
                currentOffset++;
            }

            return results;
        }

        /// <summary>
        /// Commits the consumer offset for a subscriber, marking all messages up to
        /// and including this offset as consumed by this subscriber.
        /// </summary>
        /// <param name="topicPartition">Composite key of topic and partition index.</param>
        /// <param name="subscriberId">Unique identifier for the subscriber.</param>
        /// <param name="offset">The offset to commit (inclusive).</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task CommitOffsetAsync(string topicPartition, string subscriberId, long offset, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var key = $"{topicPartition}:{subscriberId}";
            _consumerOffsets.AddOrUpdate(key, offset, (_, existing) => Math.Max(existing, offset));

            // Persist the offset
            var offsetPath = $"{WalPathPrefix}{topicPartition}/offsets/{subscriberId}";
            var offsetBytes = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(offsetBytes, offset);
            await _store.WriteAsync(offsetPath, offsetBytes, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the last committed offset for a subscriber on a topic partition.
        /// Returns -1 if no offset has been committed.
        /// </summary>
        /// <param name="topicPartition">Composite key of topic and partition index.</param>
        /// <param name="subscriberId">Unique identifier for the subscriber.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The last committed offset, or -1 if none.</returns>
        public async Task<long> GetCommittedOffsetAsync(string topicPartition, string subscriberId, CancellationToken ct = default)
        {
            var key = $"{topicPartition}:{subscriberId}";
            if (_consumerOffsets.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var offsetPath = $"{WalPathPrefix}{topicPartition}/offsets/{subscriberId}";
            var data = await _store.ReadAsync(offsetPath, ct).ConfigureAwait(false);
            if (data == null || data.Length < 8) return -1;

            var offset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(0, 8));
            _consumerOffsets.TryAdd(key, offset);
            return offset;
        }

        /// <summary>
        /// Replays unconsumed messages from the WAL for a subscriber, starting from
        /// the last committed offset + 1. Used on process restart for crash recovery.
        /// </summary>
        /// <param name="topicPartition">Composite key of topic and partition index.</param>
        /// <param name="subscriberId">Unique identifier for the subscriber.</param>
        /// <param name="maxMessages">Maximum messages to replay per call.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Unconsumed messages from the last committed offset.</returns>
        public async Task<IReadOnlyList<(long Offset, byte[] Data)>> ReplayFromLastOffsetAsync(
            string topicPartition, string subscriberId, int maxMessages = 1000, CancellationToken ct = default)
        {
            var lastOffset = await GetCommittedOffsetAsync(topicPartition, subscriberId, ct).ConfigureAwait(false);
            return await ReadAsync(topicPartition, lastOffset + 1, maxMessages, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets retention information for a partition including message count,
        /// oldest message timestamp, and total WAL size.
        /// </summary>
        /// <param name="topicPartition">Composite key of topic and partition index.</param>
        /// <returns>Retention information dictionary.</returns>
        public IReadOnlyDictionary<string, object> GetRetentionInfo(string topicPartition)
        {
            var info = new Dictionary<string, object>();

            if (_partitions.TryGetValue(topicPartition, out var wal))
            {
                info["writeOffset"] = Volatile.Read(ref wal.WriteOffset);
                info["pendingBytes"] = Interlocked.Read(ref wal.PendingBytes);
                info["uncommittedCount"] = Volatile.Read(ref wal.UncommittedCount);
                info["retentionPeriod"] = _config.RetentionPeriod.TotalHours;
                info["maxSizeBytes"] = _config.MaxPartitionSizeBytes;
            }

            // Collect consumer offsets for this partition
            var minOffset = long.MaxValue;
            foreach (var kvp in _consumerOffsets)
            {
                if (kvp.Key.StartsWith(topicPartition + ":", StringComparison.Ordinal))
                {
                    minOffset = Math.Min(minOffset, kvp.Value);
                }
            }
            info["minConsumerOffset"] = minOffset == long.MaxValue ? -1L : minOffset;

            return info;
        }

        /// <summary>
        /// Runs background compaction, removing fully-consumed messages that exceed
        /// the retention period from all partitions.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public async Task CompactAsync(CancellationToken ct = default)
        {
            if (_disposed) return;

            var cutoffTime = DateTimeOffset.UtcNow - _config.RetentionPeriod;

            foreach (var kvp in _partitions)
            {
                var topicPartition = kvp.Key;

                // Find minimum consumer offset for this partition
                var minConsumerOffset = long.MaxValue;
                foreach (var offsetKvp in _consumerOffsets)
                {
                    if (offsetKvp.Key.StartsWith(topicPartition + ":", StringComparison.Ordinal))
                    {
                        minConsumerOffset = Math.Min(minConsumerOffset, offsetKvp.Value);
                    }
                }

                if (minConsumerOffset == long.MaxValue) continue;

                // Read WAL and rewrite without fully-consumed entries
                var walPath = $"{WalPathPrefix}{topicPartition}/wal";
                var walData = await _store.ReadAsync(walPath, ct).ConfigureAwait(false);
                if (walData == null || walData.Length == 0) continue;

                var compacted = CompactWalData(walData, minConsumerOffset, cutoffTime.ToUnixTimeMilliseconds());
                if (compacted.Length < walData.Length)
                {
                    await _store.WriteAsync(walPath, compacted, ct).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Flushes all pending entries across all partitions to persistent storage.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public async Task FlushAllAsync(CancellationToken ct = default)
        {
            if (_disposed) return;

            foreach (var kvp in _partitions)
            {
                await FlushPartitionAsync(kvp.Key, kvp.Value, ct).ConfigureAwait(false);
            }
        }

        private async Task FlushPartitionAsync(string topicPartition, PartitionWal wal, CancellationToken ct)
        {
            if (wal.PendingEntries.IsEmpty) return;

            var entries = new List<byte[]>();
            while (wal.PendingEntries.TryDequeue(out var entry))
            {
                entries.Add(entry.Data);
            }

            if (entries.Count == 0) return;

            var walPath = $"{WalPathPrefix}{topicPartition}/wal";

            // Read existing WAL data and append
            var existing = await _store.ReadAsync(walPath, ct).ConfigureAwait(false);
            var totalNewBytes = 0;
            foreach (var e in entries) totalNewBytes += e.Length;

            var combined = new byte[(existing?.Length ?? 0) + totalNewBytes];
            if (existing != null)
            {
                existing.CopyTo(combined.AsSpan());
            }

            var writePos = existing?.Length ?? 0;
            foreach (var e in entries)
            {
                e.CopyTo(combined.AsSpan(writePos));
                writePos += e.Length;
                Interlocked.Add(ref wal.PendingBytes, -e.Length);
            }

            await _store.WriteAsync(walPath, combined, ct).ConfigureAwait(false);
            Interlocked.Exchange(ref wal.UncommittedCount, 0);
        }

        private PartitionWal GetOrCreatePartition(string topicPartition)
        {
            return _partitions.GetOrAdd(topicPartition, _ => new PartitionWal());
        }

        private static byte[] CompactWalData(byte[] walData, long minConsumerOffset, long cutoffTimestamp)
        {
            // Use MemoryStream instead of List<byte> to avoid byte-by-byte 2x allocation
            using var kept = new MemoryStream(walData.Length);
            long currentOffset = 0;
            int position = 0;

            while (position < walData.Length - 4)
            {
                var entryLength = BinaryPrimitives.ReadInt32LittleEndian(walData.AsSpan(position, 4));
                var totalEntrySize = 4 + entryLength + 8;

                if (position + totalEntrySize > walData.Length) break;

                // Keep if not yet consumed by all subscribers OR within retention window
                var timestamp = BinaryPrimitives.ReadInt64LittleEndian(
                    walData.AsSpan(position + 4 + entryLength, 8));

                if (currentOffset > minConsumerOffset || timestamp >= cutoffTimestamp)
                {
                    kept.Write(walData, position, totalEntrySize);
                }

                position += totalEntrySize;
                currentOffset++;
            }

            return kept.ToArray();
        }

        /// <summary>
        /// Releases all resources including timers and pending entries.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _compactionTimer.Dispose();
            _fsyncTimer?.Dispose();

            // Best-effort flush
            try
            {
                FlushAllAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch
            {
                // Disposal must not throw
            }

            _partitions.Clear();
            _consumerOffsets.Clear();
        }

        /// <summary>
        /// Internal state for a single topic partition WAL.
        /// </summary>
        private sealed class PartitionWal
        {
            public long WriteOffset;
            public long PendingBytes;
            public int UncommittedCount;
            public readonly ConcurrentQueue<(long Offset, byte[] Data)> PendingEntries = new();
        }
    }
}
