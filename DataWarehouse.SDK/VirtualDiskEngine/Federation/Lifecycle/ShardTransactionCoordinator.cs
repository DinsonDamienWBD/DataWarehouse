using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Infrastructure.Distributed;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation.Lifecycle;

/// <summary>
/// Phase of a distributed two-phase commit transaction across shards.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 93: Shard Lifecycle - Transaction Coordination")]
public enum TransactionPhase : byte
{
    /// <summary>Transaction has been created but not yet started preparation.</summary>
    Initiated = 0,

    /// <summary>Coordinator is sending prepare requests to all participants.</summary>
    Preparing = 1,

    /// <summary>All participants have voted to commit; ready for commit phase.</summary>
    Prepared = 2,

    /// <summary>Coordinator is sending commit requests to all participants.</summary>
    Committing = 3,

    /// <summary>All participants have committed; transaction is complete.</summary>
    Committed = 4,

    /// <summary>Coordinator is sending abort requests to all participants.</summary>
    Aborting = 5,

    /// <summary>Transaction has been aborted; all participants rolled back.</summary>
    Aborted = 6,

    /// <summary>Transaction exceeded its timeout and was forcibly aborted.</summary>
    TimedOut = 7
}

/// <summary>
/// Vote cast by a participant shard during the prepare phase.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 93: Shard Lifecycle - Transaction Coordination")]
public enum ParticipantVote : byte
{
    /// <summary>No vote has been cast yet.</summary>
    None = 0,

    /// <summary>Participant is prepared and votes to commit.</summary>
    VoteCommit = 1,

    /// <summary>Participant cannot prepare and votes to abort.</summary>
    VoteAbort = 2,

    /// <summary>Participant did not respond within the timeout period.</summary>
    NoResponse = 3
}

/// <summary>
/// Represents a single participant shard in a distributed transaction.
/// Tracks the shard's identity, vote, commit status, and timing.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 93: Shard Lifecycle - Transaction Coordination")]
public sealed class ShardTransactionParticipant
{
    /// <summary>Gets the unique identifier of the participating shard.</summary>
    public Guid ShardId { get; }

    /// <summary>Gets or sets the vote cast by this participant during the prepare phase.</summary>
    public ParticipantVote Vote { get; internal set; }

    /// <summary>Gets or sets the reason the participant voted to abort, if applicable.</summary>
    public string? AbortReason { get; internal set; }

    /// <summary>Gets or sets whether this participant has committed its prepared writes.</summary>
    public bool Committed { get; internal set; }

    /// <summary>Gets or sets the UTC timestamp when this participant entered the prepared state.</summary>
    public DateTimeOffset? PreparedAtUtc { get; internal set; }

    /// <summary>
    /// Creates a new participant record for the given shard.
    /// </summary>
    /// <param name="shardId">The unique identifier of the participating shard.</param>
    public ShardTransactionParticipant(Guid shardId)
    {
        ShardId = shardId;
        Vote = ParticipantVote.None;
    }
}

/// <summary>
/// Represents a cross-shard distributed transaction with 2PC lifecycle state,
/// participant tracking, timeout enforcement, and binary serialization.
/// </summary>
/// <remarks>
/// <para>
/// Binary layout (header = 64 bytes):
/// [0..15] TransactionId (Guid), [16] Phase, [17..23] reserved,
/// [24..31] CreatedAtUtcTicks (long), [32..39] TimeoutTicks (long),
/// [40..43] ParticipantCount (int), [44..63] reserved.
/// </para>
/// <para>
/// Each participant = 32 bytes:
/// [0..15] ShardId (Guid), [16] Vote, [17] Committed (byte), [18..31] reserved.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 93: Shard Lifecycle - Transaction Coordination")]
public sealed class ShardTransaction
{
    /// <summary>Header size in bytes for binary serialization.</summary>
    public const int HeaderSize = 64;

    /// <summary>Per-participant size in bytes for binary serialization.</summary>
    public const int ParticipantSize = 32;

    private volatile TransactionPhase _phase;

    /// <summary>Gets the unique identifier for this transaction.</summary>
    public Guid TransactionId { get; }

    /// <summary>Gets the current phase of the transaction. Thread-safe via volatile backing.</summary>
    public TransactionPhase Phase
    {
        get => _phase;
        internal set => _phase = value;
    }

    /// <summary>Gets the list of participant shards in this transaction.</summary>
    public IReadOnlyList<ShardTransactionParticipant> Participants { get; }

    /// <summary>Gets the UTC timestamp when this transaction was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Gets the maximum duration before this transaction is considered timed out.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Gets or sets the reason the transaction was aborted, if applicable.</summary>
    public string? AbortReason { get; internal set; }

    /// <summary>
    /// Gets whether this transaction has reached a terminal state
    /// (Committed, Aborted, or TimedOut).
    /// </summary>
    public bool IsTerminal => _phase is TransactionPhase.Committed
        or TransactionPhase.Aborted
        or TransactionPhase.TimedOut;

    /// <summary>
    /// Gets whether this transaction has exceeded its timeout period.
    /// Computed from CreatedAtUtc + Timeout vs current UTC time.
    /// </summary>
    public bool IsTimedOut => DateTimeOffset.UtcNow > CreatedAtUtc + Timeout;

    /// <summary>
    /// Creates a new shard transaction with the given participants and timeout.
    /// </summary>
    /// <param name="participants">The list of participant shards.</param>
    /// <param name="timeout">The maximum duration for this transaction.</param>
    /// <exception cref="ArgumentNullException">Thrown when participants is null.</exception>
    /// <exception cref="ArgumentException">Thrown when participants is empty or timeout is not positive.</exception>
    public ShardTransaction(IReadOnlyList<ShardTransactionParticipant> participants, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(participants);
        if (participants.Count == 0)
            throw new ArgumentException("At least one participant is required.", nameof(participants));
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentException("Timeout must be a positive duration.", nameof(timeout));

        TransactionId = Guid.NewGuid();
        Participants = participants;
        Timeout = timeout;
        CreatedAtUtc = DateTimeOffset.UtcNow;
        _phase = TransactionPhase.Initiated;
    }

    /// <summary>
    /// Creates a transaction from deserialized state (used by <see cref="Deserialize"/>).
    /// </summary>
    private ShardTransaction(
        Guid transactionId,
        TransactionPhase phase,
        IReadOnlyList<ShardTransactionParticipant> participants,
        DateTimeOffset createdAtUtc,
        TimeSpan timeout)
    {
        TransactionId = transactionId;
        _phase = phase;
        Participants = participants;
        CreatedAtUtc = createdAtUtc;
        Timeout = timeout;
    }

    /// <summary>
    /// Serializes this transaction to a binary representation using BinaryPrimitives.
    /// </summary>
    /// <returns>The serialized byte array.</returns>
    public byte[] Serialize()
    {
        int totalSize = HeaderSize + (Participants.Count * ParticipantSize);
        var buffer = new byte[totalSize];
        var span = buffer.AsSpan();

        // Header: [0..15] TransactionId
        TransactionId.TryWriteBytes(span[..16]);

        // [16] Phase
        span[16] = (byte)_phase;

        // [17..23] reserved (zero-filled by default)

        // [24..31] CreatedAtUtcTicks
        BinaryPrimitives.WriteInt64LittleEndian(span[24..32], CreatedAtUtc.UtcTicks);

        // [32..39] TimeoutTicks
        BinaryPrimitives.WriteInt64LittleEndian(span[32..40], Timeout.Ticks);

        // [40..43] ParticipantCount
        BinaryPrimitives.WriteInt32LittleEndian(span[40..44], Participants.Count);

        // [44..63] reserved

        // Participants
        for (int i = 0; i < Participants.Count; i++)
        {
            var participant = Participants[i];
            int offset = HeaderSize + (i * ParticipantSize);
            var pSpan = span.Slice(offset, ParticipantSize);

            // [0..15] ShardId
            participant.ShardId.TryWriteBytes(pSpan[..16]);

            // [16] Vote
            pSpan[16] = (byte)participant.Vote;

            // [17] Committed
            pSpan[17] = participant.Committed ? (byte)1 : (byte)0;

            // [18..31] reserved
        }

        return buffer;
    }

    /// <summary>
    /// Deserializes a transaction from binary representation.
    /// </summary>
    /// <param name="data">The binary data to deserialize.</param>
    /// <returns>A reconstructed <see cref="ShardTransaction"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when the data is too short or corrupted.</exception>
    public static ShardTransaction Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new ArgumentException(
                $"Data too short for header: expected at least {HeaderSize} bytes, got {data.Length}.",
                nameof(data));

        var transactionId = new Guid(data[..16]);
        var phase = (TransactionPhase)data[16];
        long createdTicks = BinaryPrimitives.ReadInt64LittleEndian(data[24..32]);
        long timeoutTicks = BinaryPrimitives.ReadInt64LittleEndian(data[32..40]);
        int participantCount = BinaryPrimitives.ReadInt32LittleEndian(data[40..44]);

        int expectedSize = HeaderSize + (participantCount * ParticipantSize);
        if (data.Length < expectedSize)
            throw new ArgumentException(
                $"Data too short for {participantCount} participants: expected {expectedSize} bytes, got {data.Length}.",
                nameof(data));

        var participants = new ShardTransactionParticipant[participantCount];
        for (int i = 0; i < participantCount; i++)
        {
            int offset = HeaderSize + (i * ParticipantSize);
            var pSpan = data.Slice(offset, ParticipantSize);

            var shardId = new Guid(pSpan[..16]);
            var vote = (ParticipantVote)pSpan[16];
            bool committed = pSpan[17] != 0;

            var participant = new ShardTransactionParticipant(shardId)
            {
                Vote = vote,
                Committed = committed
            };
            participants[i] = participant;
        }

        var createdAtUtc = new DateTimeOffset(createdTicks, TimeSpan.Zero);
        var timeout = TimeSpan.FromTicks(timeoutTicks);

        return new ShardTransaction(transactionId, phase, participants, createdAtUtc, timeout);
    }
}

/// <summary>
/// Two-Phase Commit (2PC) coordinator for atomic multi-shard write operations.
/// Manages the full transaction lifecycle: begin (prepare), commit, abort, and timeout cleanup.
/// </summary>
/// <remarks>
/// <para>
/// Each transaction acquires distributed locks on participating shards during the prepare phase.
/// If all participants vote to commit, the coordinator transitions to the commit phase.
/// If any participant votes to abort, the entire transaction is aborted and all locks released.
/// </para>
/// <para>
/// Expired transactions are cleaned up via <see cref="TimeoutExpiredTransactionsAsync"/>,
/// which should be called periodically by a background task.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 93: Shard Lifecycle - Transaction Coordination")]
public sealed class ShardTransactionCoordinator : IAsyncDisposable
{
    private readonly IShardVdeAccessor _shardAccessor;
    private readonly IDistributedLockService _lockService;
    private readonly FederationOptions _options;
    private readonly BoundedDictionary<Guid, ShardTransaction> _activeTransactions;
    private readonly string _lockOwner;
    private volatile bool _disposed;

    /// <summary>
    /// Creates a new 2PC transaction coordinator.
    /// </summary>
    /// <param name="shardAccessor">Provides access to individual shard VDE instances.</param>
    /// <param name="lockService">Distributed lock service for participant-level mutual exclusion.</param>
    /// <param name="options">Federation configuration including timeouts.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public ShardTransactionCoordinator(
        IShardVdeAccessor shardAccessor,
        IDistributedLockService lockService,
        FederationOptions options)
    {
        ArgumentNullException.ThrowIfNull(shardAccessor);
        ArgumentNullException.ThrowIfNull(lockService);
        ArgumentNullException.ThrowIfNull(options);

        _shardAccessor = shardAccessor;
        _lockService = lockService;
        _options = options;
        _activeTransactions = new BoundedDictionary<Guid, ShardTransaction>(4096);
        _lockOwner = $"txn-coord-{Environment.MachineName}";
    }

    /// <summary>
    /// Begins a new distributed transaction: creates participant records and executes
    /// the prepare phase by acquiring distributed locks on all participating shards.
    /// </summary>
    /// <param name="participantShardIds">The shard IDs that will participate in this transaction.</param>
    /// <param name="timeout">Optional transaction timeout. Defaults to 2x ShardOperationTimeout.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The transaction in Prepared state if all participants voted commit.</returns>
    /// <exception cref="ArgumentNullException">Thrown when participantShardIds is null.</exception>
    /// <exception cref="ArgumentException">Thrown when participantShardIds is empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when any participant votes to abort.</exception>
    public async Task<ShardTransaction> BeginTransactionAsync(
        IReadOnlyList<Guid> participantShardIds,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(participantShardIds);
        if (participantShardIds.Count == 0)
            throw new ArgumentException("At least one participant shard is required.", nameof(participantShardIds));
        ObjectDisposedException.ThrowIf(_disposed, this);

        var effectiveTimeout = timeout ?? TimeSpan.FromTicks(_options.ShardOperationTimeout.Ticks * 2);
        if (effectiveTimeout <= TimeSpan.Zero)
            effectiveTimeout = TimeSpan.FromSeconds(60);

        var participants = new ShardTransactionParticipant[participantShardIds.Count];
        for (int i = 0; i < participantShardIds.Count; i++)
        {
            participants[i] = new ShardTransactionParticipant(participantShardIds[i]);
        }

        var transaction = new ShardTransaction(participants, effectiveTimeout);
        _activeTransactions[transaction.TransactionId] = transaction;

        // Transition to Preparing
        transaction.Phase = TransactionPhase.Preparing;

        // Prepare phase: acquire distributed lock for each participant
        Guid? failedShardId = null;
        string? failedReason = null;

        for (int i = 0; i < participants.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var participant = participants[i];
            var lockId = $"shard-txn:{participant.ShardId}:{transaction.TransactionId}";

            try
            {
                var acquiredLock = await _lockService.TryAcquireAsync(
                    lockId,
                    _lockOwner,
                    effectiveTimeout,
                    ct).ConfigureAwait(false);

                if (acquiredLock != null)
                {
                    participant.Vote = ParticipantVote.VoteCommit;
                    participant.PreparedAtUtc = DateTimeOffset.UtcNow;
                }
                else
                {
                    participant.Vote = ParticipantVote.VoteAbort;
                    participant.AbortReason = "Failed to acquire distributed lock";
                    failedShardId = participant.ShardId;
                    failedReason = participant.AbortReason;
                    break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                participant.Vote = ParticipantVote.VoteAbort;
                participant.AbortReason = $"Lock acquisition failed: {ex.Message}";
                failedShardId = participant.ShardId;
                failedReason = participant.AbortReason;
                break;
            }
        }

        // Check if any participant voted abort
        if (failedShardId.HasValue)
        {
            await AbortTransactionAsync(transaction.TransactionId, ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Transaction aborted: participant {failedShardId.Value} voted abort: {failedReason}");
        }

        // All participants voted commit
        transaction.Phase = TransactionPhase.Prepared;
        return transaction;
    }

    /// <summary>
    /// Commits a prepared transaction: signals all participant shards to finalize
    /// their prepared writes, then releases distributed locks and removes the transaction.
    /// </summary>
    /// <param name="transactionId">The ID of the transaction to commit.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when the transaction is not in the Prepared phase.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the transaction ID is not found.</exception>
    public async Task CommitTransactionAsync(Guid transactionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_activeTransactions.TryGetValue(transactionId, out var transaction))
            throw new KeyNotFoundException($"Transaction {transactionId} not found in active transactions.");

        if (transaction.Phase != TransactionPhase.Prepared)
            throw new InvalidOperationException(
                $"Cannot commit transaction in phase {transaction.Phase}. Expected Prepared.");

        // Transition to Committing
        transaction.Phase = TransactionPhase.Committing;

        // Mark all participants as committed
        for (int i = 0; i < transaction.Participants.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            transaction.Participants[i].Committed = true;
        }

        // Transition to Committed
        transaction.Phase = TransactionPhase.Committed;

        // Release all distributed locks
        await ReleaseAllLocksAsync(transaction, ct).ConfigureAwait(false);

        // Remove from active transactions
        _activeTransactions.TryRemove(transactionId, out _);
    }

    /// <summary>
    /// Aborts a transaction: releases all distributed locks regardless of current state,
    /// transitions to Aborted, and removes from active transactions.
    /// </summary>
    /// <param name="transactionId">The ID of the transaction to abort.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task AbortTransactionAsync(Guid transactionId, CancellationToken ct = default)
    {
        if (!_activeTransactions.TryGetValue(transactionId, out var transaction))
            return;

        // Transition to Aborting
        transaction.Phase = TransactionPhase.Aborting;

        // Release all distributed locks (ignore failures)
        await ReleaseAllLocksAsync(transaction, CancellationToken.None).ConfigureAwait(false);

        // Transition to Aborted
        transaction.Phase = TransactionPhase.Aborted;

        // Remove from active transactions
        _activeTransactions.TryRemove(transactionId, out _);
    }

    /// <summary>
    /// Gets a transaction by its ID from the active transaction store.
    /// </summary>
    /// <param name="transactionId">The transaction ID to look up.</param>
    /// <returns>The transaction if found; null otherwise.</returns>
    public ShardTransaction? GetTransaction(Guid transactionId)
    {
        return _activeTransactions.TryPeek(transactionId, out var transaction) ? transaction : null;
    }

    /// <summary>
    /// Returns all active (non-terminal) transactions.
    /// </summary>
    /// <returns>A read-only list of non-terminal transactions.</returns>
    public IReadOnlyList<ShardTransaction> GetActiveTransactions()
    {
        var result = new List<ShardTransaction>();
        foreach (var kvp in _activeTransactions)
        {
            if (!kvp.Value.IsTerminal)
            {
                result.Add(kvp.Value);
            }
        }
        return result;
    }

    /// <summary>
    /// Checks all active transactions for timeout and aborts any that have exceeded
    /// their timeout period. Should be called periodically by a background task.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task TimeoutExpiredTransactionsAsync(CancellationToken ct = default)
    {
        var expiredIds = new List<Guid>();

        foreach (var kvp in _activeTransactions)
        {
            if (!kvp.Value.IsTerminal && kvp.Value.IsTimedOut)
            {
                expiredIds.Add(kvp.Key);
            }
        }

        for (int i = 0; i < expiredIds.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var txnId = expiredIds[i];

            if (_activeTransactions.TryPeek(txnId, out var transaction))
            {
                transaction.Phase = TransactionPhase.TimedOut;
                transaction.AbortReason = "Transaction exceeded timeout period";
                await AbortTransactionAsync(txnId, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Disposes the coordinator by aborting all active transactions and releasing resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Abort all active transactions
        var activeIds = new List<Guid>();
        foreach (var kvp in _activeTransactions)
        {
            if (!kvp.Value.IsTerminal)
            {
                activeIds.Add(kvp.Key);
            }
        }

        for (int i = 0; i < activeIds.Count; i++)
        {
            try
            {
                await AbortTransactionAsync(activeIds[i], CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup during disposal
            }
        }

        await _activeTransactions.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Releases all distributed locks held by participants in the given transaction.
    /// Lock release failures are silently ignored to ensure cleanup completes.
    /// </summary>
    private async Task ReleaseAllLocksAsync(ShardTransaction transaction, CancellationToken ct)
    {
        for (int i = 0; i < transaction.Participants.Count; i++)
        {
            var participant = transaction.Participants[i];
            var lockId = $"shard-txn:{participant.ShardId}:{transaction.TransactionId}";

            try
            {
                await _lockService.ReleaseAsync(lockId, _lockOwner, ct).ConfigureAwait(false);
            }
            catch
            {
                // Ignore lock release failures — locks will expire via lease timeout
            }
        }
    }
}
