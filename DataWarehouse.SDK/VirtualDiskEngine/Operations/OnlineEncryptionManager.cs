using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Operations;

/// <summary>
/// Result of beginning an encryption migration (encrypt, decrypt, or rekey).
/// Contains the OPJR operation identifier and Superblock field values.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: online encryption migration (VOPT-76)")]
public readonly struct EncryptionMigrationOperation : IEquatable<EncryptionMigrationOperation>
{
    /// <summary>OPJR operation identifier for this migration.</summary>
    public ulong OperationId { get; }

    /// <summary>
    /// Key slot index being migrated to (encrypt/rekey) or from (decrypt).
    /// Stored in the Superblock EncryptionMigrationKeySlot field.
    /// </summary>
    public uint EncryptionMigrationKeySlot { get; }

    /// <summary>
    /// Current progress in units of <see cref="OnlineEncryptionManager.MigrationChunkBlocks"/>
    /// blocks.  Stored in the Superblock EncryptionMigrationProgress field.
    /// </summary>
    public uint EncryptionMigrationProgress { get; }

    /// <summary>The type of encryption operation (Encrypt, Decrypt, or Rekey).</summary>
    public OperationType MigrationType { get; }

    /// <summary>Constructs an encryption migration operation result.</summary>
    public EncryptionMigrationOperation(
        ulong operationId,
        uint encryptionMigrationKeySlot,
        uint encryptionMigrationProgress,
        OperationType migrationType)
    {
        OperationId = operationId;
        EncryptionMigrationKeySlot = encryptionMigrationKeySlot;
        EncryptionMigrationProgress = encryptionMigrationProgress;
        MigrationType = migrationType;
    }

    /// <inheritdoc />
    public bool Equals(EncryptionMigrationOperation other) =>
        OperationId == other.OperationId
        && EncryptionMigrationKeySlot == other.EncryptionMigrationKeySlot
        && EncryptionMigrationProgress == other.EncryptionMigrationProgress
        && MigrationType == other.MigrationType;

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is EncryptionMigrationOperation other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(OperationId, EncryptionMigrationKeySlot, MigrationType);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(EncryptionMigrationOperation left, EncryptionMigrationOperation right) =>
        left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(EncryptionMigrationOperation left, EncryptionMigrationOperation right) =>
        !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"EncryptionMigration(Op={OperationId}, Type={MigrationType}, KeySlot={EncryptionMigrationKeySlot}, Progress={EncryptionMigrationProgress})";
}

/// <summary>
/// Checkpoint data returned after processing a chunk of encryption migration.
/// The caller writes <see cref="ProgressForSuperblock"/> into the Superblock
/// EncryptionMigrationProgress field.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: online encryption migration (VOPT-76)")]
public readonly struct EncryptionMigrationCheckpoint : IEquatable<EncryptionMigrationCheckpoint>
{
    /// <summary>Number of chunks fully processed so far (cumulative).</summary>
    public ulong CompletedChunks { get; }

    /// <summary>
    /// Value for the Superblock EncryptionMigrationProgress field.
    /// Expressed in units of <see cref="OnlineEncryptionManager.MigrationChunkBlocks"/> blocks.
    /// </summary>
    public uint ProgressForSuperblock { get; }

    /// <summary>Constructs a checkpoint result.</summary>
    public EncryptionMigrationCheckpoint(ulong completedChunks, uint progressForSuperblock)
    {
        CompletedChunks = completedChunks;
        ProgressForSuperblock = progressForSuperblock;
    }

    /// <inheritdoc />
    public bool Equals(EncryptionMigrationCheckpoint other) =>
        CompletedChunks == other.CompletedChunks && ProgressForSuperblock == other.ProgressForSuperblock;

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is EncryptionMigrationCheckpoint other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(CompletedChunks, ProgressForSuperblock);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(EncryptionMigrationCheckpoint left, EncryptionMigrationCheckpoint right) =>
        left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(EncryptionMigrationCheckpoint left, EncryptionMigrationCheckpoint right) =>
        !left.Equals(right);
}

/// <summary>
/// Recovery information for an in-progress encryption migration found after a crash.
/// Used by the mount path to resume from the last checkpoint.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: online encryption migration (VOPT-76)")]
public readonly struct EncryptionRecoveryInfo : IEquatable<EncryptionRecoveryInfo>
{
    /// <summary>OPJR operation identifier.</summary>
    public ulong OperationId { get; }

    /// <summary>The type of encryption operation (Encrypt, Decrypt, or Rekey).</summary>
    public OperationType MigrationType { get; }

    /// <summary>Number of chunks already processed before the crash.</summary>
    public ulong CompletedChunks { get; }

    /// <summary>
    /// Key slot involved in the migration.  For Rekey operations this is the
    /// <em>old</em> key slot; the new key slot is in the OPJR payload.
    /// </summary>
    public uint KeySlot { get; }

    /// <summary>Constructs recovery info.</summary>
    public EncryptionRecoveryInfo(
        ulong operationId,
        OperationType migrationType,
        ulong completedChunks,
        uint keySlot)
    {
        OperationId = operationId;
        MigrationType = migrationType;
        CompletedChunks = completedChunks;
        KeySlot = keySlot;
    }

    /// <inheritdoc />
    public bool Equals(EncryptionRecoveryInfo other) =>
        OperationId == other.OperationId
        && MigrationType == other.MigrationType
        && CompletedChunks == other.CompletedChunks
        && KeySlot == other.KeySlot;

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is EncryptionRecoveryInfo other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(OperationId, MigrationType, KeySlot);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(EncryptionRecoveryInfo left, EncryptionRecoveryInfo right) =>
        left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(EncryptionRecoveryInfo left, EncryptionRecoveryInfo right) =>
        !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"EncryptionRecovery(Op={OperationId}, Type={MigrationType}, Completed={CompletedChunks}, KeySlot={KeySlot})";
}

/// <summary>
/// Manages online encryption operations (encrypt, decrypt, rekey) on a live VDE volume
/// with crash-safe progress tracking via the Operation Journal Region (OPJR).
///
/// Encryption migration progress is tracked in units of
/// <see cref="MigrationChunkBlocks"/> blocks (1024 blocks = 4 MB at 4 KB block size)
/// per the DWVD v2.1 spec.  The caller is responsible for:
///   1. Performing the actual block-level encryption/decryption.
///   2. Writing the returned Superblock field values (EncryptionMigrationKeySlot
///      and EncryptionMigrationProgress) after each checkpoint.
///   3. Clearing the Superblock fields when migration completes.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: online encryption migration (VOPT-76)")]
public sealed class OnlineEncryptionManager
{
    /// <summary>
    /// Progress granularity: 1024 blocks per chunk (4 MB at 4 KB block size).
    /// The Superblock EncryptionMigrationProgress field is expressed in these units.
    /// </summary>
    public const int MigrationChunkBlocks = 1024;

    /// <summary>
    /// Sentinel value for the Superblock EncryptionMigrationKeySlot field
    /// indicating no encryption migration is in progress.
    /// </summary>
    public const uint NoMigrationInProgress = 0xFFFFFFFF;

    private readonly OperationJournalRegion _journal;
    private readonly int _blockSize;

    /// <summary>
    /// Initialises the encryption manager.
    /// </summary>
    /// <param name="journal">The OPJR region to record operations in.</param>
    /// <param name="blockSize">Volume block size in bytes (e.g. 4096).</param>
    /// <exception cref="ArgumentNullException"><paramref name="journal"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockSize"/> is not positive.</exception>
    public OnlineEncryptionManager(OperationJournalRegion journal, int blockSize)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(blockSize, 0, nameof(blockSize));
        _blockSize = blockSize;
    }

    /// <summary>
    /// Begins an online encryption of a previously unencrypted volume.
    /// Creates an OPJR entry of type <see cref="OperationType.Encrypt"/> and transitions
    /// it to <see cref="OperationState.InProgress"/>.
    /// </summary>
    /// <param name="keySlot">Key slot index for the new encryption key.</param>
    /// <param name="totalBlocks">Total number of blocks to encrypt.</param>
    /// <returns>Operation info including the OPJR identifier and Superblock field values.</returns>
    public EncryptionMigrationOperation BeginEncrypt(uint keySlot, ulong totalBlocks)
    {
        return BeginMigration(OperationType.Encrypt, keySlot, totalBlocks, BuildKeySlotPayload(keySlot));
    }

    /// <summary>
    /// Begins an online decryption of an encrypted volume.
    /// Creates an OPJR entry of type <see cref="OperationType.Decrypt"/> and transitions
    /// it to <see cref="OperationState.InProgress"/>.
    /// </summary>
    /// <param name="keySlot">Key slot index of the current encryption key.</param>
    /// <param name="totalBlocks">Total number of blocks to decrypt.</param>
    /// <returns>Operation info including the OPJR identifier and Superblock field values.</returns>
    public EncryptionMigrationOperation BeginDecrypt(uint keySlot, ulong totalBlocks)
    {
        return BeginMigration(OperationType.Decrypt, keySlot, totalBlocks, BuildKeySlotPayload(keySlot));
    }

    /// <summary>
    /// Begins an online rekey of an already-encrypted volume, migrating from
    /// <paramref name="oldKeySlot"/> to <paramref name="newKeySlot"/>.
    /// Creates an OPJR entry of type <see cref="OperationType.Rekey"/> and transitions
    /// it to <see cref="OperationState.InProgress"/>.
    /// </summary>
    /// <param name="oldKeySlot">Key slot index of the current encryption key.</param>
    /// <param name="newKeySlot">Key slot index of the new encryption key.</param>
    /// <param name="totalBlocks">Total number of blocks to rekey.</param>
    /// <returns>Operation info including the OPJR identifier and Superblock field values.</returns>
    public EncryptionMigrationOperation BeginRekey(uint oldKeySlot, uint newKeySlot, ulong totalBlocks)
    {
        byte[] payload = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), oldKeySlot);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), newKeySlot);
        return BeginMigration(OperationType.Rekey, oldKeySlot, totalBlocks, payload);
    }

    /// <summary>
    /// Processes a checkpoint after one or more chunks have been encrypted/decrypted.
    /// Updates the OPJR progress and returns the Superblock EncryptionMigrationProgress value.
    /// </summary>
    /// <param name="operationId">OPJR operation identifier from <see cref="BeginEncrypt"/>,
    /// <see cref="BeginDecrypt"/>, or <see cref="BeginRekey"/>.</param>
    /// <param name="chunksProcessedSoFar">Total number of chunks (units of
    /// <see cref="MigrationChunkBlocks"/> blocks) processed so far (cumulative).</param>
    /// <returns>Checkpoint data for Superblock update.</returns>
    /// <exception cref="KeyNotFoundException">Operation not found in OPJR.</exception>
    public EncryptionMigrationCheckpoint ProcessChunk(ulong operationId, ulong chunksProcessedSoFar)
    {
        // Checkpoint the OPJR with completed units and the chunk index as checkpoint data.
        _journal.CheckpointProgress(
            operationId,
            completedUnits: chunksProcessedSoFar,
            checkpointData: chunksProcessedSoFar);

        // The Superblock EncryptionMigrationProgress is a uint32 in units of
        // MigrationChunkBlocks (1024 blocks per spec).
        uint progressForSuperblock = chunksProcessedSoFar <= uint.MaxValue
            ? (uint)chunksProcessedSoFar
            : uint.MaxValue;

        return new EncryptionMigrationCheckpoint(chunksProcessedSoFar, progressForSuperblock);
    }

    /// <summary>
    /// Marks an encryption migration as completed.  The caller must then clear the
    /// Superblock EncryptionMigrationKeySlot to <see cref="NoMigrationInProgress"/>
    /// (0xFFFFFFFF) and EncryptionMigrationProgress to 0.
    /// </summary>
    /// <param name="operationId">OPJR operation identifier.</param>
    /// <exception cref="InvalidOperationException">State transition failed.</exception>
    public void CompleteMigration(ulong operationId)
    {
        if (!_journal.TransitionState(operationId, OperationState.Completed))
        {
            throw new InvalidOperationException(
                $"Failed to complete encryption migration operation {operationId}. " +
                "The operation may not exist or is already in a terminal state.");
        }
    }

    /// <summary>
    /// Checks the OPJR for any in-progress encryption migration that was interrupted
    /// by a crash.  Returns recovery information if found, or <see langword="null"/>
    /// if no pending migration exists.
    /// </summary>
    /// <returns>Recovery info for the pending migration, or null.</returns>
    public EncryptionRecoveryInfo? CheckForPendingMigration()
    {
        var pending = _journal.GetOperationsRequiringRecovery();

        foreach (var entry in pending)
        {
            if (entry.Type is not (OperationType.Encrypt or OperationType.Decrypt or OperationType.Rekey))
                continue;

            // Extract key slot from the OPJR payload (first 4 bytes LE).
            uint keySlot = 0;
            if (entry.OperationPayload.Length >= 4)
            {
                keySlot = BinaryPrimitives.ReadUInt32LittleEndian(entry.OperationPayload.AsSpan(0, 4));
            }

            return new EncryptionRecoveryInfo(
                operationId: entry.OperationId,
                migrationType: entry.Type,
                completedChunks: entry.CompletedUnits,
                keySlot: keySlot);
        }

        return null;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private EncryptionMigrationOperation BeginMigration(
        OperationType type,
        uint keySlot,
        ulong totalBlocks,
        byte[] payload)
    {
        ulong totalChunks = (totalBlocks + (ulong)MigrationChunkBlocks - 1) / (ulong)MigrationChunkBlocks;

        ulong operationId = _journal.CreateOperation(
            type: type,
            sourceStart: 0,
            targetStart: 0,
            totalUnits: totalChunks,
            payload: payload);

        // Transition from Queued to InProgress immediately.
        _journal.TransitionState(operationId, OperationState.InProgress);

        return new EncryptionMigrationOperation(
            operationId: operationId,
            encryptionMigrationKeySlot: keySlot,
            encryptionMigrationProgress: 0,
            migrationType: type);
    }

    private static byte[] BuildKeySlotPayload(uint keySlot)
    {
        byte[] payload = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), keySlot);
        return payload;
    }
}
