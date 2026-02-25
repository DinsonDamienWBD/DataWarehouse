using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure.Distributed
{
    /// <summary>
    /// In-memory implementation of <see cref="IRaftLogStore"/> for testing, single-node deployments,
    /// and backward compatibility with the existing Raft engine behavior.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Log entries are stored in a <see cref="List{T}"/> and are NOT durable across restarts.
    /// This is suitable for:
    /// <list type="bullet">
    /// <item><description>Unit and integration testing</description></item>
    /// <item><description>Single-node development clusters</description></item>
    /// <item><description>Backward compatibility (original behavior before IRaftLogStore)</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// For production multi-node clusters, use <see cref="FileRaftLogStore"/>
    /// which provides fsync-backed durability.
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 65: In-memory Raft log store for testing/single-node")]
    internal sealed class InMemoryRaftLogStore : IRaftLogStore
    {
        private readonly List<RaftLogEntry> _log = new();
        private readonly object _lock = new();
        private long _currentTerm;
        private string? _votedFor;

        /// <inheritdoc/>
        public long Count
        {
            get
            {
                lock (_lock) { return _log.Count; }
            }
        }

        /// <inheritdoc/>
        public Task AppendAsync(RaftLogEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            lock (_lock)
            {
                _log.Add(entry);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<RaftLogEntry?> GetAsync(long index)
        {
            lock (_lock)
            {
                if (index <= 0 || index > _log.Count)
                    return Task.FromResult<RaftLogEntry?>(null);

                return Task.FromResult<RaftLogEntry?>(_log[(int)(index - 1)]);
            }
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<RaftLogEntry>> GetRangeAsync(long fromIndex, long toIndex)
        {
            lock (_lock)
            {
                if (fromIndex > _log.Count || fromIndex > toIndex)
                    return Task.FromResult<IReadOnlyList<RaftLogEntry>>(Array.Empty<RaftLogEntry>());

                int start = (int)Math.Max(0, fromIndex - 1);
                int end = (int)Math.Min(_log.Count, toIndex);
                int count = end - start;

                if (count <= 0)
                    return Task.FromResult<IReadOnlyList<RaftLogEntry>>(Array.Empty<RaftLogEntry>());

                return Task.FromResult<IReadOnlyList<RaftLogEntry>>(_log.GetRange(start, count).AsReadOnly());
            }
        }

        /// <inheritdoc/>
        public Task TruncateFromAsync(long fromIndex)
        {
            lock (_lock)
            {
                if (fromIndex <= 0 || fromIndex > _log.Count)
                    return Task.CompletedTask;

                int start = (int)(fromIndex - 1);
                _log.RemoveRange(start, _log.Count - start);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<long> GetLastIndexAsync()
        {
            lock (_lock)
            {
                return Task.FromResult(_log.Count > 0 ? _log[^1].Index : 0L);
            }
        }

        /// <inheritdoc/>
        public Task<long> GetLastTermAsync()
        {
            lock (_lock)
            {
                return Task.FromResult(_log.Count > 0 ? _log[^1].Term : 0L);
            }
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<RaftLogEntry>> GetFromAsync(long fromIndex)
        {
            lock (_lock)
            {
                if (fromIndex > _log.Count)
                    return Task.FromResult<IReadOnlyList<RaftLogEntry>>(Array.Empty<RaftLogEntry>());

                int start = (int)Math.Max(0, fromIndex - 1);
                return Task.FromResult<IReadOnlyList<RaftLogEntry>>(
                    _log.GetRange(start, _log.Count - start).AsReadOnly());
            }
        }

        /// <inheritdoc/>
        public Task<(long term, string? votedFor)> GetPersistentStateAsync()
        {
            lock (_lock)
            {
                return Task.FromResult((_currentTerm, _votedFor));
            }
        }

        /// <inheritdoc/>
        public Task SavePersistentStateAsync(long term, string? votedFor)
        {
            lock (_lock)
            {
                _currentTerm = term;
                _votedFor = votedFor;
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task CompactAsync(long upToIndex)
        {
            lock (_lock)
            {
                if (upToIndex <= 0 || upToIndex > _log.Count)
                    return Task.CompletedTask;

                _log.RemoveRange(0, (int)upToIndex);

                // Re-index remaining entries starting from 1
                for (int i = 0; i < _log.Count; i++)
                {
                    _log[i].Index = i + 1;
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Gets the internal log list for direct access (used by RaftConsensusEngine for backward compatibility).
        /// </summary>
        internal List<RaftLogEntry> InternalLog => _log;

        /// <summary>
        /// Removes entries from the beginning of the log (used for compaction).
        /// </summary>
        /// <param name="start">Zero-based start index of entries to remove.</param>
        /// <param name="count">Number of entries to remove.</param>
        internal void RemoveRange(int start, int count)
        {
            lock (_lock)
            {
                _log.RemoveRange(start, count);
            }
        }
    }
}
