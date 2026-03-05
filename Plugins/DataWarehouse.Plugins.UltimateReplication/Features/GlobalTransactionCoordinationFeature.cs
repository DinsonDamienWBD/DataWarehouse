using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateReplication.Features
{
    /// <summary>
    /// Global Transaction Coordination Feature (C1).
    /// Implements two-phase commit (2PC) and three-phase commit (3PC) coordination
    /// across replication nodes via message bus for distributed transaction guarantees.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Supports two coordination protocols:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>2PC (Two-Phase Commit)</b>: Prepare -> Commit/Abort. Suitable for small participant sets.</item>
    ///   <item><b>3PC (Three-Phase Commit)</b>: CanCommit -> PreCommit -> DoCommit. Non-blocking with timeout recovery.</item>
    /// </list>
    /// <para>
    /// All inter-node communication uses the message bus. No direct plugin references.
    /// Coordinator tracks participant votes, applies configurable timeouts, and handles
    /// partial failures with automatic rollback.
    /// </para>
    /// </remarks>
    public sealed class GlobalTransactionCoordinationFeature : IDisposable
    {
        private readonly ReplicationStrategyRegistry _registry;
        private readonly IMessageBus _messageBus;
        private readonly BoundedDictionary<string, TransactionState> _activeTransactions = new BoundedDictionary<string, TransactionState>(1000);
        private readonly BoundedDictionary<string, TransactionLog> _transactionLog = new BoundedDictionary<string, TransactionLog>(1000);
        // P2-3702: TCS per transaction replaces the 50 ms busy-wait polling loop in WaitForVotesAsync.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>> _voteTcs =
            new System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>>();
        private readonly TimeSpan _prepareTimeout;
        private readonly TimeSpan _commitTimeout;
        private bool _disposed;
        private IDisposable? _subscription;

        // Topics
        private const string TxnPrepareTopic = "replication.ultimate.txn.prepare";
        private const string TxnPrepareResponseTopic = "replication.ultimate.txn.prepare.response";
        private const string TxnCommitTopic = "replication.ultimate.txn.commit";
        private const string TxnAbortTopic = "replication.ultimate.txn.abort";
        private const string TxnCanCommitTopic = "replication.ultimate.txn.cancommit";
        private const string TxnPreCommitTopic = "replication.ultimate.txn.precommit";

        // Statistics
        private long _totalTransactions;
        private long _committedTransactions;
        private long _abortedTransactions;
        private long _timedOutTransactions;

        /// <summary>
        /// Initializes a new instance of the GlobalTransactionCoordinationFeature.
        /// </summary>
        /// <param name="registry">The replication strategy registry.</param>
        /// <param name="messageBus">Message bus for inter-node communication.</param>
        /// <param name="prepareTimeout">Timeout for prepare phase. Defaults to 30 seconds.</param>
        /// <param name="commitTimeout">Timeout for commit phase. Defaults to 60 seconds.</param>
        public GlobalTransactionCoordinationFeature(
            ReplicationStrategyRegistry registry,
            IMessageBus messageBus,
            TimeSpan? prepareTimeout = null,
            TimeSpan? commitTimeout = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
            _prepareTimeout = prepareTimeout ?? TimeSpan.FromSeconds(30);
            _commitTimeout = commitTimeout ?? TimeSpan.FromSeconds(60);

            _subscription = _messageBus.Subscribe(TxnPrepareResponseTopic, HandlePrepareResponseAsync);
        }

        /// <summary>Gets total transactions initiated.</summary>
        public long TotalTransactions => Interlocked.Read(ref _totalTransactions);

        /// <summary>Gets committed transaction count.</summary>
        public long CommittedTransactions => Interlocked.Read(ref _committedTransactions);

        /// <summary>Gets aborted transaction count.</summary>
        public long AbortedTransactions => Interlocked.Read(ref _abortedTransactions);

        /// <summary>
        /// Executes a distributed replication transaction using two-phase commit (2PC).
        /// </summary>
        /// <param name="transactionId">Unique transaction identifier. Generated if null.</param>
        /// <param name="participantNodes">Node IDs participating in the transaction.</param>
        /// <param name="data">Data to replicate in this transaction.</param>
        /// <param name="metadata">Optional metadata for the transaction.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Transaction result with commit/abort status and participant outcomes.</returns>
        public async Task<TransactionResult> ExecuteTwoPhaseCommitAsync(
            string? transactionId,
            IReadOnlyList<string> participantNodes,
            ReadOnlyMemory<byte> data,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken ct = default)
        {
            transactionId ??= GenerateTransactionId();
            Interlocked.Increment(ref _totalTransactions);

            var state = new TransactionState
            {
                TransactionId = transactionId,
                Protocol = TransactionProtocol.TwoPhaseCommit,
                Phase = TransactionPhase.Prepare,
                ParticipantNodes = participantNodes.ToList(),
                Data = data,
                Metadata = metadata ?? new Dictionary<string, string>(),
                CreatedAt = DateTimeOffset.UtcNow,
                Votes = new BoundedDictionary<string, ParticipantVote>(1000)
            };

            _activeTransactions[transactionId] = state;
            LogTransaction(transactionId, "2PC initiated", participantNodes);

            // Phase 1: Prepare
            var prepareSuccess = await SendPrepareAndCollectVotesAsync(state, ct);

            if (prepareSuccess)
            {
                // Phase 2: Commit
                state.Phase = TransactionPhase.Commit;
                await BroadcastCommitAsync(state, ct);
                state.Phase = TransactionPhase.Committed;
                Interlocked.Increment(ref _committedTransactions);
                LogTransaction(transactionId, "2PC committed", participantNodes);
            }
            else
            {
                // Phase 2: Abort
                state.Phase = TransactionPhase.Abort;
                await BroadcastAbortAsync(state, ct);
                state.Phase = TransactionPhase.Aborted;
                Interlocked.Increment(ref _abortedTransactions);
                LogTransaction(transactionId, "2PC aborted", participantNodes);
            }

            _activeTransactions.TryRemove(transactionId, out _);

            return new TransactionResult
            {
                TransactionId = transactionId,
                Protocol = TransactionProtocol.TwoPhaseCommit,
                Outcome = prepareSuccess ? TransactionOutcome.Committed : TransactionOutcome.Aborted,
                ParticipantOutcomes = state.Votes.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value),
                Duration = DateTimeOffset.UtcNow - state.CreatedAt,
                CompletedAt = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Executes a distributed replication transaction using three-phase commit (3PC).
        /// Non-blocking variant with CanCommit, PreCommit, DoCommit phases.
        /// </summary>
        /// <param name="transactionId">Unique transaction identifier. Generated if null.</param>
        /// <param name="participantNodes">Node IDs participating in the transaction.</param>
        /// <param name="data">Data to replicate in this transaction.</param>
        /// <param name="metadata">Optional metadata for the transaction.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Transaction result with commit/abort status and participant outcomes.</returns>
        public async Task<TransactionResult> ExecuteThreePhaseCommitAsync(
            string? transactionId,
            IReadOnlyList<string> participantNodes,
            ReadOnlyMemory<byte> data,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken ct = default)
        {
            transactionId ??= GenerateTransactionId();
            Interlocked.Increment(ref _totalTransactions);

            var state = new TransactionState
            {
                TransactionId = transactionId,
                Protocol = TransactionProtocol.ThreePhaseCommit,
                Phase = TransactionPhase.CanCommit,
                ParticipantNodes = participantNodes.ToList(),
                Data = data,
                Metadata = metadata ?? new Dictionary<string, string>(),
                CreatedAt = DateTimeOffset.UtcNow,
                Votes = new BoundedDictionary<string, ParticipantVote>(1000)
            };

            _activeTransactions[transactionId] = state;
            LogTransaction(transactionId, "3PC initiated", participantNodes);

            // Phase 1: CanCommit
            var canCommitSuccess = await SendCanCommitAndCollectVotesAsync(state, ct);

            if (!canCommitSuccess)
            {
                state.Phase = TransactionPhase.Aborted;
                Interlocked.Increment(ref _abortedTransactions);
                await BroadcastAbortAsync(state, ct);
                _activeTransactions.TryRemove(transactionId, out _);
                LogTransaction(transactionId, "3PC aborted at CanCommit", participantNodes);

                return BuildTransactionResult(state, TransactionOutcome.Aborted);
            }

            // Phase 2: PreCommit
            state.Phase = TransactionPhase.PreCommit;
            // Replace the Votes dictionary rather than calling Clear() to avoid a race
            // between Clear() and concurrent HandlePrepareResponseAsync writes (finding 3704).
            state.Votes = new BoundedDictionary<string, ParticipantVote>(1000);
            var preCommitSuccess = await SendPreCommitAndCollectAcksAsync(state, ct);

            if (!preCommitSuccess)
            {
                state.Phase = TransactionPhase.Aborted;
                Interlocked.Increment(ref _abortedTransactions);
                await BroadcastAbortAsync(state, ct);
                _activeTransactions.TryRemove(transactionId, out _);
                LogTransaction(transactionId, "3PC aborted at PreCommit", participantNodes);

                return BuildTransactionResult(state, TransactionOutcome.Aborted);
            }

            // Phase 3: DoCommit
            state.Phase = TransactionPhase.Commit;
            await BroadcastCommitAsync(state, ct);
            state.Phase = TransactionPhase.Committed;
            Interlocked.Increment(ref _committedTransactions);
            _activeTransactions.TryRemove(transactionId, out _);
            LogTransaction(transactionId, "3PC committed", participantNodes);

            return BuildTransactionResult(state, TransactionOutcome.Committed);
        }

        /// <summary>
        /// Gets the current state of an active transaction.
        /// </summary>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <returns>The transaction state, or null if not found.</returns>
        public TransactionState? GetTransactionState(string transactionId)
        {
            return _activeTransactions.GetValueOrDefault(transactionId);
        }

        /// <summary>
        /// Gets the transaction log for auditing.
        /// </summary>
        /// <returns>All recorded transaction log entries.</returns>
        public IReadOnlyDictionary<string, TransactionLog> GetTransactionLog()
        {
            return _transactionLog;
        }

        #region Private Methods

        private async Task<bool> SendPrepareAndCollectVotesAsync(TransactionState state, CancellationToken ct)
        {
            var preparePayload = new Dictionary<string, object>
            {
                ["transactionId"] = state.TransactionId,
                ["protocol"] = "2PC",
                ["phase"] = "prepare",
                ["dataSize"] = state.Data.Length,
                ["dataHash"] = ComputeSha256(state.Data.Span),
                ["participantCount"] = state.ParticipantNodes.Count
            };

            foreach (var node in state.ParticipantNodes)
            {
                preparePayload["targetNode"] = node;
                await _messageBus.PublishAsync(TxnPrepareTopic, new PluginMessage
                {
                    Type = TxnPrepareTopic,
                    CorrelationId = state.TransactionId,
                    Source = "replication.ultimate.coordinator",
                    Payload = new Dictionary<string, object>(preparePayload)
                }, ct);
            }

            return await WaitForVotesAsync(state, _prepareTimeout, ct);
        }

        private async Task<bool> SendCanCommitAndCollectVotesAsync(TransactionState state, CancellationToken ct)
        {
            var payload = new Dictionary<string, object>
            {
                ["transactionId"] = state.TransactionId,
                ["protocol"] = "3PC",
                ["phase"] = "cancommit",
                ["dataSize"] = state.Data.Length,
                ["dataHash"] = ComputeSha256(state.Data.Span),
                ["participantCount"] = state.ParticipantNodes.Count
            };

            foreach (var node in state.ParticipantNodes)
            {
                payload["targetNode"] = node;
                await _messageBus.PublishAsync(TxnCanCommitTopic, new PluginMessage
                {
                    Type = TxnCanCommitTopic,
                    CorrelationId = state.TransactionId,
                    Source = "replication.ultimate.coordinator",
                    Payload = new Dictionary<string, object>(payload)
                }, ct);
            }

            return await WaitForVotesAsync(state, _prepareTimeout, ct);
        }

        private async Task<bool> SendPreCommitAndCollectAcksAsync(TransactionState state, CancellationToken ct)
        {
            var payload = new Dictionary<string, object>
            {
                ["transactionId"] = state.TransactionId,
                ["protocol"] = "3PC",
                ["phase"] = "precommit"
            };

            foreach (var node in state.ParticipantNodes)
            {
                payload["targetNode"] = node;
                await _messageBus.PublishAsync(TxnPreCommitTopic, new PluginMessage
                {
                    Type = TxnPreCommitTopic,
                    CorrelationId = state.TransactionId,
                    Source = "replication.ultimate.coordinator",
                    Payload = new Dictionary<string, object>(payload)
                }, ct);
            }

            return await WaitForVotesAsync(state, _prepareTimeout, ct);
        }

        private async Task<bool> WaitForVotesAsync(TransactionState state, TimeSpan timeout, CancellationToken ct)
        {
            // P2-3702: use a TaskCompletionSource so this method suspends without a polling loop.
            // HandlePrepareResponseAsync signals the TCS when all expected votes have arrived.
            var tcs = _voteTcs.GetOrAdd(state.TransactionId,
                _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            try
            {
                using (cts.Token.Register(() => tcs.TrySetCanceled(ct)))
                {
                    await tcs.Task.ConfigureAwait(false);
                }
                return state.Votes.Values.All(v => v == ParticipantVote.Yes);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timed out (not caller cancellation)
                Interlocked.Increment(ref _timedOutTransactions);
                foreach (var node in state.ParticipantNodes)
                    state.Votes.TryAdd(node, ParticipantVote.Timeout);
                return false;
            }
            finally
            {
                _voteTcs.TryRemove(state.TransactionId, out _);
            }
        }

        private async Task BroadcastCommitAsync(TransactionState state, CancellationToken ct)
        {
            var payload = new Dictionary<string, object>
            {
                ["transactionId"] = state.TransactionId,
                ["phase"] = "commit"
            };

            foreach (var node in state.ParticipantNodes)
            {
                payload["targetNode"] = node;
                await _messageBus.PublishAsync(TxnCommitTopic, new PluginMessage
                {
                    Type = TxnCommitTopic,
                    CorrelationId = state.TransactionId,
                    Source = "replication.ultimate.coordinator",
                    Payload = new Dictionary<string, object>(payload)
                }, ct);
            }
        }

        private async Task BroadcastAbortAsync(TransactionState state, CancellationToken ct)
        {
            var payload = new Dictionary<string, object>
            {
                ["transactionId"] = state.TransactionId,
                ["phase"] = "abort"
            };

            foreach (var node in state.ParticipantNodes)
            {
                payload["targetNode"] = node;
                await _messageBus.PublishAsync(TxnAbortTopic, new PluginMessage
                {
                    Type = TxnAbortTopic,
                    CorrelationId = state.TransactionId,
                    Source = "replication.ultimate.coordinator",
                    Payload = new Dictionary<string, object>(payload)
                }, ct);
            }
        }

        private Task HandlePrepareResponseAsync(PluginMessage message)
        {
            var txnId = message.CorrelationId;
            if (txnId == null || !_activeTransactions.TryGetValue(txnId, out var state))
                return Task.CompletedTask;

            var nodeId = message.Payload.GetValueOrDefault("nodeId")?.ToString() ?? message.Source;
            var voteStr = message.Payload.GetValueOrDefault("vote")?.ToString() ?? "no";
            var vote = voteStr.Equals("yes", StringComparison.OrdinalIgnoreCase)
                ? ParticipantVote.Yes
                : ParticipantVote.No;

            state.Votes[nodeId] = vote;

            // P2-3702: signal the TCS when all participant votes have arrived so WaitForVotesAsync
            // can unblock without the 50 ms polling loop.
            if (state.Votes.Count >= state.ParticipantNodes.Count &&
                _voteTcs.TryGetValue(txnId, out var tcs))
            {
                tcs.TrySetResult(true);
            }

            return Task.CompletedTask;
        }

        private void LogTransaction(string txnId, string action, IEnumerable<string> participants)
        {
            _transactionLog[txnId] = new TransactionLog
            {
                TransactionId = txnId,
                Action = action,
                Participants = participants.ToArray(),
                Timestamp = DateTimeOffset.UtcNow
            };
        }

        private static TransactionResult BuildTransactionResult(TransactionState state, TransactionOutcome outcome)
        {
            return new TransactionResult
            {
                TransactionId = state.TransactionId,
                Protocol = state.Protocol,
                Outcome = outcome,
                ParticipantOutcomes = state.Votes.ToDictionary(kv => kv.Key, kv => kv.Value),
                Duration = DateTimeOffset.UtcNow - state.CreatedAt,
                CompletedAt = DateTimeOffset.UtcNow
            };
        }

        private static string GenerateTransactionId()
        {
            return $"txn-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}"[..32];
        }

        private static string ComputeSha256(ReadOnlySpan<byte> data)
        {
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(data, hash);
            return Convert.ToHexString(hash);
        }

        #endregion

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _subscription?.Dispose();
        }
    }

    #region Transaction Types

    /// <summary>
    /// Distributed transaction coordination protocol.
    /// </summary>
    public enum TransactionProtocol
    {
        /// <summary>Two-phase commit: Prepare -> Commit/Abort.</summary>
        TwoPhaseCommit,
        /// <summary>Three-phase commit: CanCommit -> PreCommit -> DoCommit (non-blocking).</summary>
        ThreePhaseCommit
    }

    /// <summary>
    /// Current phase of a distributed transaction.
    /// </summary>
    public enum TransactionPhase
    {
        /// <summary>Initial state.</summary>
        Init,
        /// <summary>3PC: CanCommit query sent.</summary>
        CanCommit,
        /// <summary>2PC/3PC: Prepare/vote phase.</summary>
        Prepare,
        /// <summary>3PC: PreCommit acknowledgments.</summary>
        PreCommit,
        /// <summary>Commit phase.</summary>
        Commit,
        /// <summary>Abort phase.</summary>
        Abort,
        /// <summary>Transaction committed.</summary>
        Committed,
        /// <summary>Transaction aborted.</summary>
        Aborted
    }

    /// <summary>
    /// Participant vote in a distributed transaction.
    /// </summary>
    public enum ParticipantVote
    {
        /// <summary>Participant agrees to commit.</summary>
        Yes,
        /// <summary>Participant votes to abort.</summary>
        No,
        /// <summary>Participant did not respond within timeout.</summary>
        Timeout
    }

    /// <summary>
    /// Outcome of a distributed transaction.
    /// </summary>
    public enum TransactionOutcome
    {
        /// <summary>Transaction committed successfully.</summary>
        Committed,
        /// <summary>Transaction aborted.</summary>
        Aborted,
        /// <summary>Transaction timed out.</summary>
        TimedOut
    }

    /// <summary>
    /// State of an active distributed transaction.
    /// </summary>
    public sealed class TransactionState
    {
        /// <summary>Transaction identifier.</summary>
        public required string TransactionId { get; init; }
        /// <summary>Protocol being used.</summary>
        public required TransactionProtocol Protocol { get; init; }
        /// <summary>Current phase (volatile via Interlocked to prevent torn reads across threads).</summary>
        private int _phase;
        public TransactionPhase Phase
        {
            get => (TransactionPhase)Volatile.Read(ref _phase);
            set => Volatile.Write(ref _phase, (int)value);
        }
        /// <summary>Participating node IDs.</summary>
        public required List<string> ParticipantNodes { get; init; }
        /// <summary>Transaction data payload.</summary>
        public required ReadOnlyMemory<byte> Data { get; init; }
        /// <summary>Transaction metadata.</summary>
        public required IReadOnlyDictionary<string, string> Metadata { get; init; }
        /// <summary>
        /// Participant votes. Replace the entire dictionary (rather than calling Clear()) when
        /// starting a new voting phase to avoid races between Clear() and concurrent writes.
        /// </summary>
        public BoundedDictionary<string, ParticipantVote> Votes { get; set; } = new(1000);
        /// <summary>When transaction was created.</summary>
        public DateTimeOffset CreatedAt { get; init; }
    }

    /// <summary>
    /// Result of a distributed transaction.
    /// </summary>
    public sealed class TransactionResult
    {
        /// <summary>Transaction identifier.</summary>
        public required string TransactionId { get; init; }
        /// <summary>Protocol used.</summary>
        public required TransactionProtocol Protocol { get; init; }
        /// <summary>Final outcome.</summary>
        public required TransactionOutcome Outcome { get; init; }
        /// <summary>Per-participant outcomes.</summary>
        public required Dictionary<string, ParticipantVote> ParticipantOutcomes { get; init; }
        /// <summary>Total transaction duration.</summary>
        public required TimeSpan Duration { get; init; }
        /// <summary>When transaction completed.</summary>
        public required DateTimeOffset CompletedAt { get; init; }
    }

    /// <summary>
    /// Transaction log entry for audit trail.
    /// </summary>
    public sealed class TransactionLog
    {
        /// <summary>Transaction identifier.</summary>
        public required string TransactionId { get; init; }
        /// <summary>Action performed.</summary>
        public required string Action { get; init; }
        /// <summary>Participating nodes.</summary>
        public required string[] Participants { get; init; }
        /// <summary>Timestamp of log entry.</summary>
        public required DateTimeOffset Timestamp { get; init; }
    }

    #endregion
}
